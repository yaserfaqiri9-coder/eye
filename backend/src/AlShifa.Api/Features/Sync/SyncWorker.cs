using System.Data;
using System.Data.Common;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Settings;
using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace AlShifa.Api.Features.Sync;

public sealed class SyncOptions
{
    /// <summary>The online copy, for example https://shafa.novatechsoft.com. Empty means sync is off.</summary>
    public string Url { get; set; } = "";

    /// <summary>Shared with the online server's Mirror:SyncKey. Kept in the hospital server's secrets, never in Git.</summary>
    public string Key { get; set; } = "";

    public int IntervalMinutes { get; set; } = 5;

    public int BatchSize { get; set; } = 500;

    /// <summary>Lets the hospital application start first.</summary>
    public int StartDelaySeconds { get; set; } = 20;
}

public sealed record SyncStatusDto(
    string Mode, bool Enabled, DateTime? LastSuccessAt, DateTime? LastAttemptAt, string? LastError, int? Pending, int IntervalMinutes, bool? Connected);

/// <summary>
/// Copies changed rows from the hospital database to the online copy, one way only. Runs beside the application and never inside a request:
/// without internet, entries wait in SyncChanges and leave once the connection is back. Re-sending is harmless, since each row is written as it is now.
/// </summary>
public sealed class SyncWorker(IServiceScopeFactory scopes, IHttpClientFactory httpFactory, IOptions<SyncOptions> options, IConfiguration config, IHostEnvironment env, ILogger<SyncWorker> logger)
    : BackgroundService
{
    public const string HttpClientName = "AlShifa.Sync";
    private readonly SemaphoreSlim running = new(1, 1);
    private readonly SemaphoreSlim wake = new(0, 1);

    public void RequestRun()
    {
        if (wake.CurrentCount == 0)
        {
            wake.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The hospital comes first: a slow start of the sync must never delay the application.
        await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, options.Value.StartDelaySeconds)), stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                // Logged and retried at the next interval. The sync must never stop the hospital application.
                try
                {
                    logger.LogWarning("Sync to the online copy did not finish: {Problem}", Describe(e));
                }
                catch
                {
                    // Logging itself failed; the next round tries again.
                }
            }

            await WaitForNextRunAsync(stoppingToken);
        }
    }

    /// <summary>Until the interval passes or someone asks for a run now. Never throws: a failure here would stop the whole application.</summary>
    private async Task WaitForNextRunAsync(CancellationToken stoppingToken)
    {
        try
        {
            await wake.WaitAsync(TimeSpan.FromMinutes(Math.Max(1, options.Value.IntervalMinutes)), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        await running.WaitAsync(ct);
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await SetStateAsync(db, "push.lastAttemptAt", DateTime.UtcNow.ToString("o"), ct);
            try
            {
                using var http = httpFactory.CreateClient(HttpClientName);
                http.BaseAddress = new Uri(options.Value.Url.TrimEnd('/') + "/");
                http.Timeout = TimeSpan.FromSeconds(120);

                var schema = SyncSchema.SchemaVersion(db);
                var remote = await MirrorStateAsync(http, ct);
                if (remote.Schema != schema)
                {
                    // Nothing is sent and nothing is lost: changes wait here until both sides run the same version.
                    throw new InvalidOperationException($"schema_mismatch: online copy is at {remote.Schema}, this server at {schema}. Update the online copy (deploy-release.sh), then this server.");
                }

                // The online copy lost batches it had confirmed (rebuilt or restored), or holds batches this database never sent
                // (a test, or this database was restored from a backup): it is emptied and filled again.
                var acked = ReadLong(await GetStateAsync(db, "push.ackedSeq", ct));
                var sent = ReadLong(await GetStateAsync(db, "push.seq", ct));
                var reset = remote.Seq < acked || remote.Seq > sent;

                // A first run, a new version whose migration may have changed existing rows, or a reset sends everything once.
                if (reset || await GetStateAsync(db, "push.snapshotOf", ct) != schema)
                {
                    await SnapshotAsync(db, http, schema, reset, ct);
                    await SetStateAsync(db, "push.snapshotOf", schema, ct);
                }

                var pushed = await PushChangesAsync(db, http, schema, ct);
                await PushFilesAsync(db, http, ct);

                // Nothing changed: an empty batch still tells the online copy the hospital is connected and its data is current.
                if (!pushed)
                {
                    await SendAsync(http, schema, [], ct);
                }
                await SetStateAsync(db, "push.lastSuccessAt", DateTime.UtcNow.ToString("o"), ct);
                await SetStateAsync(db, "push.lastError", null, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                await SetStateAsync(db, "push.lastError", Describe(e), CancellationToken.None);
                throw;
            }
        }
        finally
        {
            running.Release();
        }
    }

    private async Task SnapshotAsync(AppDbContext db, HttpClient http, string schema, bool reset, CancellationToken ct)
    {
        logger.LogInformation("Sending a full copy of the database to the online copy{Reset}.", reset ? ", which is emptied first" : "");
        if (reset)
        {
            // Its own batch, so the online copy is emptied even when there is nothing to send.
            await SendAsync(http, schema, [], ct, reset: true);
        }

        foreach (var table in SyncSchema.Tables(db))
        {
            var columns = SyncSchema.SyncedColumns(table);
            var keys = SyncSchema.KeyColumns(table);
            object? after = null;
            while (true)
            {
                // Pages follow a single-column key. Tables with a composite key are small link tables and go in one piece.
                var paged = keys.Count == 1;
                var sql = $"SELECT {string.Join(",", columns.Select(c => SyncSchema.Quote(c.Name)))} FROM {SyncSchema.Quote(table.Name)}"
                    + (paged && after is not null ? $" WHERE {SyncSchema.Quote(keys[0].Name)} > @after" : "")
                    + (paged ? $" ORDER BY {SyncSchema.Quote(keys[0].Name)} LIMIT {options.Value.BatchSize}" : "");
                var rows = await ReadAsync(db, null, sql, after is null ? [] : [("@after", after)], ct);
                if (rows.Count > 0)
                {
                    await SendAsync(http, schema, [new SyncTableRows(table.Name, columns.Select(c => c.Name).ToArray(), rows.Select(r => r.Select(SyncValues.ToJson).ToArray()).ToList(), [])], ct);
                }

                if (!paged || rows.Count < options.Value.BatchSize)
                {
                    break;
                }

                after = rows[^1][columns.ToList().FindIndex(c => c.Name == keys[0].Name)];
            }
        }
    }

    /// <summary>True when at least one batch went out.</summary>
    private async Task<bool> PushChangesAsync(AppDbContext db, HttpClient http, string schema, CancellationToken ct)
    {
        var tables = SyncSchema.Tables(db).ToDictionary(t => t.Name, StringComparer.Ordinal);
        var sent = false;
        while (true)
        {
            var changes = await db.SyncChanges.AsNoTracking().OrderBy(c => c.Id).Take(options.Value.BatchSize).ToListAsync(ct);
            if (changes.Count == 0)
            {
                return sent;
            }

            var batch = new List<SyncTableRows>();

            // One consistent view of the database for the whole batch: an invoice and its lines are read as they stood together.
            await using (var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct))
            {
                foreach (var group in changes.GroupBy(c => c.TableName))
                {
                    if (!tables.TryGetValue(group.Key, out var table))
                    {
                        logger.LogWarning("Sync entries for unknown table {Table} skipped.", group.Key);
                        continue;
                    }

                    var columns = SyncSchema.SyncedColumns(table);
                    var keyColumns = SyncSchema.KeyColumns(table);
                    var keyIndexes = keyColumns.Select(k => columns.ToList().FindIndex(c => c.Name == k.Name)).ToList();
                    var upserts = new List<JsonElement[]>();
                    var deletes = new List<JsonElement[]>();
                    foreach (var keyJson in group.Select(c => c.KeyJson).Distinct())
                    {
                        var key = JsonSerializer.Deserialize<JsonElement[]>(keyJson)!;
                        var keyValues = keyColumns.Select((c, i) => SyncValues.ToDb(key[i], c)).ToList();
                        var where = string.Join(" AND ", keyColumns.Select((k, i) => $"{SyncSchema.Quote(k.Name)}=@k{i}"));
                        var sql = $"SELECT {string.Join(",", columns.Select(c => SyncSchema.Quote(c.Name)))} FROM {SyncSchema.Quote(table.Name)} WHERE {where}";
                        var rows = await ReadAsync(db, tx, sql, keyValues.Select((v, i) => ($"@k{i}", v)), ct);

                        // A row that is gone was deleted; one that exists is sent as it is now, whatever happened to it before.
                        if (rows.Count == 0)
                        {
                            deletes.Add(keyValues.Select(SyncValues.ToJson).ToArray());
                        }
                        else
                        {
                            upserts.Add(rows[0].Select(SyncValues.ToJson).ToArray());
                        }
                    }

                    batch.Add(new SyncTableRows(table.Name, columns.Select(c => c.Name).ToArray(), upserts, deletes));
                }

                await tx.CommitAsync(ct);
            }

            if (batch.Count > 0)
            {
                await SendAsync(http, schema, batch, ct);
                sent = true;
            }

            // Only what was sent is removed; anything recorded meanwhile waits for the next round.
            var ids = changes.Select(c => c.Id).ToList();
            await db.SyncChanges.Where(c => ids.Contains(c.Id)).ExecuteDeleteAsync(ct);
        }
    }

    private async Task PushFilesAsync(AppDbContext db, HttpClient http, CancellationToken ct)
    {
        using var request = await SignedAsync(HttpMethod.Get, "api/sync/mirror/files/missing", [], ct);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        var missing = await response.Content.ReadFromJsonAsync<List<string>>(ct) ?? [];
        var root = SettingsEndpoints.StorageRoot(config, env);
        foreach (var name in missing)
        {
            var path = Path.Combine(root, Path.GetFileName(name));
            if (!File.Exists(path))
            {
                logger.LogWarning("Attachment file {Name} is missing on the hospital server too.", name);
                continue;
            }

            using var upload = await SignedAsync(HttpMethod.Put, $"api/sync/mirror/files/{Uri.EscapeDataString(name)}", await File.ReadAllBytesAsync(path, ct), ct);
            using var uploaded = await http.SendAsync(upload, ct);
            await EnsureSuccessAsync(uploaded, ct);
        }
    }

    private async Task SendAsync(HttpClient http, string schema, List<SyncTableRows> tables, CancellationToken ct, bool reset = false)
    {
        var seq = await NextSeqAsync(ct);
        var body = JsonSerializer.SerializeToUtf8Bytes(new SyncBatch(schema, seq, DateTime.UtcNow, tables, reset), JsonSerializerOptions.Web);
        using var request = await SignedAsync(HttpMethod.Post, "api/sync/mirror/apply", body, ct);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);

        // Confirmed by the online copy. If it later reports less, it lost data and gets everything again.
        using var scope = scopes.CreateScope();
        await SetStateAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), "push.ackedSeq", seq.ToString(CultureInfo.InvariantCulture), ct);
    }

    private async Task<MirrorState> MirrorStateAsync(HttpClient http, CancellationToken ct)
    {
        using var request = await SignedAsync(HttpMethod.Get, "api/sync/mirror/state", [], ct);
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<MirrorState>(JsonSerializerOptions.Web, ct)
            ?? throw new InvalidOperationException("The online server sent an empty state.");
    }

    private static long ReadLong(string? value) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;

    /// <summary>Grows with every attempt, so a delayed retry can never overwrite a newer batch online.</summary>
    private async Task<long> NextSeqAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var last = ReadLong(await GetStateAsync(db, "push.seq", ct));
        var next = Math.Max(last + 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await SetStateAsync(db, "push.seq", next.ToString(CultureInfo.InvariantCulture), ct);
        return next;
    }

    private async Task<HttpRequestMessage> SignedAsync(HttpMethod method, string relative, byte[] body, CancellationToken ct)
    {
        var key = options.Value.Key;
        if (key.Length < 32)
        {
            throw new InvalidOperationException("Sync:Key is missing or shorter than 32 characters.");
        }

        var path = "/" + relative;
        var time = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var request = new HttpRequestMessage(method, relative);
        request.Headers.Add(SyncSignature.TimeHeader, time);
        request.Headers.Add(SyncSignature.SignatureHeader, SyncSignature.Sign(key, time, method.Method, path, body));
        if (method != HttpMethod.Get)
        {
            // Compressed on the wire; the signature covers the uncompressed bytes the server reads.
            var compressed = new MemoryStream();
            await using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                await gzip.WriteAsync(body, ct);
            }

            request.Content = new ByteArrayContent(compressed.ToArray());
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(method == HttpMethod.Post ? "application/json" : "application/octet-stream");
            request.Content.Headers.ContentEncoding.Add("gzip");
        }

        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            // The web server in front answers with an HTML page while the online application is down or restarting.
            if ((int)response.StatusCode is 502 or 503 or 504)
            {
                throw new HttpRequestException($"The online server is not available right now (HTTP {(int)response.StatusCode}). Changes wait here and are sent when it is back.");
            }

            var text = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Online server answered {(int)response.StatusCode}: {text[..Math.Min(text.Length, 400)]}");
        }
    }

    private static async Task<List<object?[]>> ReadAsync(AppDbContext db, IDbContextTransaction? tx, string sql, IEnumerable<(string Name, object? Value)> parameters, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = tx?.GetDbTransaction();
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        var rows = new List<object?[]>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    public static async Task<string?> GetStateAsync(AppDbContext db, string key, CancellationToken ct) =>
        await db.SyncStates.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).SingleOrDefaultAsync(ct);

    private static async Task SetStateAsync(AppDbContext db, string key, string? value, CancellationToken ct)
    {
        var state = await db.SyncStates.SingleOrDefaultAsync(s => s.Key == key, ct);
        if (state is null)
        {
            db.SyncStates.Add(state = new SyncState { Key = key });
        }

        state.Value = value;
        state.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private static string Describe(Exception e) => e switch
    {
        HttpRequestException { InnerException: System.Net.Sockets.SocketException } => "No connection to the online server (internet or server unavailable).",
        TaskCanceledException => "The online server did not answer in time.",
        _ => e.Message.Length > 1000 ? e.Message[..1000] : e.Message,
    };

    /// <summary>Status for Settings, read from the database so it survives a restart.</summary>
    public static async Task<SyncStatusDto> StatusAsync(AppDbContext db, SyncOptions options, bool enabled, CancellationToken ct)
    {
        var states = await db.SyncStates.AsNoTracking().Where(s => s.Key.StartsWith("push.")).ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        DateTime? Time(string key) => states.TryGetValue(key, out var v) && DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : null;
        var success = Time("push.lastSuccessAt");
        var attempt = Time("push.lastAttemptAt");
        return new SyncStatusDto(
            "local", enabled, success, attempt, states.GetValueOrDefault("push.lastError"),
            await db.SyncChanges.CountAsync(ct), options.IntervalMinutes,
            attempt is null ? null : success is not null && success >= attempt);
    }
}

/// <summary>
/// Runs at the hospital while sync is off. Changes made now are not recorded, so the marker of the last full copy is removed:
/// when sync is switched on again, everything is sent once. Retries until the database answers, since MySQL may start after the service.
/// </summary>
public sealed class SyncOffMarker(IServiceScopeFactory scopes, ILogger<SyncOffMarker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        for (var attempt = 0; !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<AppDbContext>().SyncStates.Where(s => s.Key == "push.snapshotOf").ExecuteDeleteAsync(stoppingToken);
                return;
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                if (attempt == 0)
                {
                    logger.LogInformation("Sync is off; the full-copy marker will be cleared once the database answers ({Problem}).", e.Message);
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }
}

public static class SyncEndpoints
{
    public static void MapSyncStatus(this IEndpointRouteBuilder app, bool mirror)
    {
        var group = app.MapGroup("/api/sync").WithTags("Sync");

        // Every signed-in user online sees how fresh the data is; at the hospital, the details are for those who manage settings.
        group.MapGet("/status", async (HttpContext http, AppDbContext db, IOptions<SyncOptions> options, SyncSwitch sync, CancellationToken ct) =>
        {
            if (mirror)
            {
                var states = await db.SyncStates.AsNoTracking().Where(s => s.Key.StartsWith("apply.")).ToDictionaryAsync(s => s.Key, s => s.Value, ct);
                DateTime? last = states.TryGetValue("apply.lastAt", out var v) && DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : null;
                return Results.Ok(new SyncStatusDto("mirror", true, last, last, null, null, options.Value.IntervalMinutes, null));
            }

            if (!http.User.HasClaim(RolePermissions.ClaimType, "settings.view"))
            {
                return Results.Ok(new SyncStatusDto("local", sync.Capture, null, null, null, null, options.Value.IntervalMinutes, null));
            }

            return Results.Ok(await SyncWorker.StatusAsync(db, options.Value, sync.Capture, ct));
        }).RequireAuthorization();

        if (!mirror)
        {
            group.MapPost("/run", (IServiceProvider services) =>
            {
                var worker = services.GetServices<IHostedService>().OfType<SyncWorker>().FirstOrDefault()
                    ?? throw new AppException(StatusCodes.Status409Conflict, "sync_off", "همگام‌سازی آنلاین روی این سرور تنظیم نشده است.");
                worker.RequestRun();
                return Results.Accepted();
            }).RequirePermission("settings.edit");
        }
    }
}
