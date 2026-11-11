using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Settings;
using AlShifa.Api.Features.Users;
using AlShifa.Api.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Options;
using MySql.Data.MySqlClient;

namespace AlShifa.Api.Features.Sync;

/// <summary>The online copy at the director's domain. It shows what the hospital server sent and changes nothing.</summary>
public sealed class MirrorOptions
{
    public bool Enabled { get; set; }

    /// <summary>Shared with the hospital server's Sync:Key. At least 32 characters; kept in the server's environment file, never in Git.</summary>
    public string SyncKey { get; set; } = "";

    /// <summary>The database account that may write. The application itself runs on ConnectionStrings:Default, which may only read.</summary>
    public string SyncConnection { get; set; } = "";
}

public sealed record SyncTableRows(string Table, string[] Columns, List<JsonElement[]> Upserts, List<JsonElement[]> Deletes);

/// <summary>Reset empties the online copy before this batch: the first batch of a full resend after the online copy lost data
/// or holds rows this hospital never sent (a test, or a hospital database restored from a backup).</summary>
public sealed record SyncBatch(string Schema, long Seq, DateTime SentAt, List<SyncTableRows> Tables, bool Reset = false);

public sealed record SyncApplied(int Upserted, int Deleted);

/// <summary>What the online copy holds: its database version and the last batch it applied.</summary>
public sealed record MirrorState(string Schema, long Seq);

public static class ReadOnlyMirror
{
    public static AppException Refusal() =>
        new(StatusCodes.Status403Forbidden, "read_only", "نسخه آنلاین فقط برای مشاهده است. ثبت، ویرایش و حذف فقط در سیستم داخل شفاخانه انجام می‌شود.");

    /// <summary>The only requests online that may carry a change: signing in and out, the live connection, and the hospital server's signed sync.</summary>
    private static bool MayChange(HttpRequest request) =>
        request.Path.Equals("/api/auth/login") || request.Path.Equals("/api/auth/logout")
        || request.Path.StartsWithSegments("/api/sync/mirror") || request.Path.StartsWithSegments("/hubs");

    /// <summary>Refuses every other writing request before any endpoint runs. Hidden buttons alone would not stop a crafted request.</summary>
    public static IApplicationBuilder UseReadOnlyMirror(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        var method = http.Request.Method;
        var reading = HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);
        if (!reading && !MayChange(http.Request))
        {
            http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("AlShifa.Mirror")
                .LogWarning("Online write refused: {Method} {Path} by {User} from {Address}", method, http.Request.Path, http.User.Identity?.Name, http.Connection.RemoteIpAddress);
            throw Refusal();
        }

        await next();
    });

    public static void MapMirror(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sync/mirror").WithTags("Sync").AllowAnonymous().DisableAntiforgery();

        group.MapPost("/apply", async (HttpContext http, AppDbContext db, IOptions<MirrorOptions> options, ILoggerFactory logs, CancellationToken ct) =>
        {
            var body = await SyncSignature.ReadVerifiedAsync(http, options.Value.SyncKey, 64 * 1024 * 1024, ct);
            var batch = JsonSerializer.Deserialize<SyncBatch>(body, JsonSerializerOptions.Web)
                ?? throw new AppException(StatusCodes.Status400BadRequest, "invalid_batch", "Empty batch.");

            // Rows are copied column by column, so both sides must run the same version of the database.
            var schema = SyncSchema.SchemaVersion(db);
            if (batch.Schema != schema)
            {
                throw new AppException(StatusCodes.Status409Conflict, "schema_mismatch", $"Online database is at {schema}, hospital server at {batch.Schema}. Deploy the same version online first.");
            }

            var applied = await MirrorApply.ApplyAsync(db, options.Value.SyncConnection, batch, ct);
            logs.CreateLogger("AlShifa.Mirror").LogInformation("Sync batch {Seq} applied: {Upserted} rows written, {Deleted} removed", batch.Seq, applied.Upserted, applied.Deleted);
            return Results.Ok(applied);
        });

        group.MapGet("/state", async (HttpContext http, AppDbContext db, IOptions<MirrorOptions> options, CancellationToken ct) =>
        {
            await SyncSignature.ReadVerifiedAsync(http, options.Value.SyncKey, 0, ct);
            var seq = await db.SyncStates.AsNoTracking().Where(s => s.Key == "apply.seq").Select(s => s.Value).SingleOrDefaultAsync(ct);
            return Results.Ok(new MirrorState(SyncSchema.SchemaVersion(db), long.TryParse(seq, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0));
        });

        group.MapGet("/files/missing", async (HttpContext http, AppDbContext db, IOptions<MirrorOptions> options, IConfiguration config, IHostEnvironment env, CancellationToken ct) =>
        {
            await SyncSignature.ReadVerifiedAsync(http, options.Value.SyncKey, 0, ct);
            var root = SettingsEndpoints.StorageRoot(config, env);
            var names = await db.Attachments.AsNoTracking().Select(a => a.StoredName).ToListAsync(ct);
            return Results.Ok(names.Where(n => !File.Exists(Path.Combine(root, n))).Take(200).ToList());
        });

        group.MapPut("/files/{name}", async (string name, HttpContext http, AppDbContext db, IOptions<MirrorOptions> options, IConfiguration config, IHostEnvironment env, CancellationToken ct) =>
        {
            var body = await SyncSignature.ReadVerifiedAsync(http, options.Value.SyncKey, 11 * 1024 * 1024, ct);
            if (!StoredName().IsMatch(name))
            {
                throw new AppException(StatusCodes.Status400BadRequest, "invalid_name", "Invalid file name.");
            }

            // Only a file that matches a synced attachment row is accepted, byte for byte.
            var expected = await db.Attachments.AsNoTracking().Where(a => a.StoredName == name).Select(a => a.Sha256).FirstOrDefaultAsync(ct)
                ?? throw AppException.NotFound();
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(body)), expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new AppException(StatusCodes.Status400BadRequest, "hash_mismatch", "File does not match its record.");
            }

            var root = SettingsEndpoints.StorageRoot(config, env);
            Directory.CreateDirectory(root);
            var temporary = Path.Combine(root, name + ".partial");
            await File.WriteAllBytesAsync(temporary, body, ct);
            File.Move(temporary, Path.Combine(root, name), overwrite: true);
            return Results.NoContent();
        });
    }

    private static Regex StoredName() => new("^[0-9a-f]{32}$", RegexOptions.CultureInvariant);
}

/// <summary>Online, only the "view" permissions of an account's roles are kept, so pages show no actions and every writing endpoint answers 403.</summary>
public sealed class MirrorClaimsPrincipalFactory(UserManager<AppUser> users, RoleManager<AppRole> roles, IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<AppUser, AppRole>(users, roles, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(AppUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        foreach (var claim in identity.FindAll(RolePermissions.ClaimType).Where(c => !c.Value.EndsWith(".view", StringComparison.Ordinal)).ToList())
        {
            identity.RemoveClaim(claim);
        }

        return identity;
    }
}

/// <summary>HMAC-SHA256 over time, method, path and body, with a key both servers hold. Requests older than ten minutes are refused.</summary>
public static class SyncSignature
{
    public const string TimeHeader = "X-Sync-Time";
    public const string SignatureHeader = "X-Sync-Signature";
    private static readonly TimeSpan MaxSkew = TimeSpan.FromMinutes(10);

    public static string Sign(string key, string time, string method, string path, ReadOnlySpan<byte> body)
    {
        var text = $"{time}\n{method.ToUpperInvariant()}\n{path}\n{Convert.ToHexString(SHA256.HashData(body))}";
        return Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(text)));
    }

    public static async Task<byte[]> ReadVerifiedAsync(HttpContext http, string key, long maxBytes, CancellationToken ct)
    {
        var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("AlShifa.Mirror");
        if (key.Length < 32)
        {
            logger.LogError("Mirror:SyncKey is missing or shorter than 32 characters; sync requests are refused.");
            throw new AppException(StatusCodes.Status503ServiceUnavailable, "sync_not_configured", "Sync is not configured on this server.");
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await http.Request.Body.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > maxBytes)
            {
                throw new AppException(StatusCodes.Status413PayloadTooLarge, "too_large", "Request too large.");
            }
        }

        var body = buffer.ToArray();
        var time = http.Request.Headers[TimeHeader].ToString();
        var fresh = long.TryParse(time, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            && (DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(seconds)).Duration() < MaxSkew;
        var expected = Encoding.ASCII.GetBytes(Sign(key, time, http.Request.Method, http.Request.Path.Value ?? "", body));
        var given = Encoding.ASCII.GetBytes(http.Request.Headers[SignatureHeader].ToString().ToUpperInvariant());
        if (!fresh || !CryptographicOperations.FixedTimeEquals(expected, given))
        {
            logger.LogWarning("Sync request with a bad or stale signature refused: {Method} {Path} from {Address}", http.Request.Method, http.Request.Path, http.Connection.RemoteIpAddress);
            throw new AppException(StatusCodes.Status401Unauthorized, "bad_signature", "Signature refused.");
        }

        return body;
    }
}

/// <summary>Writes one batch from the hospital server in a single transaction, so a page never shows half of it.</summary>
public static class MirrorApply
{
    public static async Task<SyncApplied> ApplyAsync(AppDbContext db, string connectionString, SyncBatch batch, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new AppException(StatusCodes.Status503ServiceUnavailable, "sync_not_configured", "Mirror:SyncConnection is not configured.");
        }

        var tables = SyncSchema.Tables(db).ToDictionary(t => t.Name, StringComparer.Ordinal);
        foreach (var rows in batch.Tables)
        {
            Validate(tables, rows);
        }

        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        // One batch at a time, and never an older one after a newer: a retried request that arrives late changes nothing.
        await ExecuteAsync(connection, tx, "INSERT INTO `SyncStates` (`Key`,`Value`,`UpdatedAt`) VALUES ('apply.seq','0',UTC_TIMESTAMP(6)) ON DUPLICATE KEY UPDATE `Key`=`Key`", [], ct);
        var last = Convert.ToInt64(await ScalarAsync(connection, tx, "SELECT `Value` FROM `SyncStates` WHERE `Key`='apply.seq' FOR UPDATE", [], ct), CultureInfo.InvariantCulture);
        // A reset is accepted whatever the number: the rows it replaces may carry numbers this hospital never used.
        // Should an old reset arrive late, the number drops below what the hospital had confirmed, and it sends everything again.
        if (batch.Seq < last && !batch.Reset)
        {
            throw new AppException(StatusCodes.Status409Conflict, "stale_batch", "A newer batch was already applied.");
        }

        var upserted = 0;
        var deleted = 0;
        try
        {
            // Rows arrive table by table, so parents may come after their children. The hospital database already enforced the links.
            await ExecuteAsync(connection, tx, "SET FOREIGN_KEY_CHECKS=0", [], ct);
            if (batch.Reset)
            {
                // Accounts stay: their online password and authenticator are set here and never come from the hospital.
                // Account rows the hospital still has are rewritten by the resend that follows.
                foreach (var table in tables.Values.Where(t => t.Name != "AspNetUsers"))
                {
                    deleted += await ExecuteAsync(connection, tx, $"DELETE FROM {SyncSchema.Quote(table.Name)}", [], ct);
                }
            }

            foreach (var rows in batch.Tables.Where(r => r.Upserts.Count > 0))
            {
                upserted += await UpsertAsync(connection, tx, tables[rows.Table], rows, ct);
            }

            await ExecuteAsync(connection, tx, "SET FOREIGN_KEY_CHECKS=1", [], ct);
            var byTable = batch.Tables.Where(r => r.Deletes.Count > 0).GroupBy(r => r.Table).ToDictionary(g => g.Key, g => g.SelectMany(r => r.Deletes).ToList());
            foreach (var table in SyncSchema.ChildrenFirst(byTable.Keys.Select(k => tables[k])))
            {
                deleted += await DeleteAsync(connection, tx, table, byTable[table.Name], ct);
            }

            await SetStateAsync(connection, tx, "apply.seq", batch.Seq.ToString(CultureInfo.InvariantCulture), ct);
            await SetStateAsync(connection, tx, "apply.lastAt", DateTime.UtcNow.ToString("o"), ct);
            await SetStateAsync(connection, tx, "apply.sourceSentAt", batch.SentAt.ToUniversalTime().ToString("o"), ct);
            await tx.CommitAsync(ct);
        }
        finally
        {
            // The connection returns to the pool; it must not keep the checks off for its next user.
            if (connection.State == ConnectionState.Open)
            {
                await ExecuteAsync(connection, null, "SET FOREIGN_KEY_CHECKS=1", [], CancellationToken.None);
            }
        }

        return new SyncApplied(upserted, deleted);
    }

    /// <summary>Only tables and columns this version knows are accepted, so no name from the request ever reaches SQL unchecked.</summary>
    private static void Validate(Dictionary<string, ITable> tables, SyncTableRows rows)
    {
        if (!tables.TryGetValue(rows.Table, out var table))
        {
            throw new AppException(StatusCodes.Status400BadRequest, "unknown_table", $"Unknown table {rows.Table}.");
        }

        var known = SyncSchema.SyncedColumns(table).Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        if (rows.Columns.Any(c => !known.Contains(c)) || rows.Columns.Distinct().Count() != rows.Columns.Length)
        {
            throw new AppException(StatusCodes.Status400BadRequest, "unknown_column", $"Unexpected columns for {rows.Table}.");
        }

        if (rows.Upserts.Count > 0 && SyncSchema.KeyColumns(table).Any(k => !rows.Columns.Contains(k.Name)))
        {
            throw new AppException(StatusCodes.Status400BadRequest, "missing_key", $"Key columns missing for {rows.Table}.");
        }

        if (rows.Upserts.Any(r => r.Length != rows.Columns.Length) || rows.Deletes.Any(d => d.Length != table.PrimaryKey!.Columns.Count))
        {
            throw new AppException(StatusCodes.Status400BadRequest, "bad_row", $"Row shape does not match {rows.Table}.");
        }
    }

    private static async Task<int> UpsertAsync(MySqlConnection connection, DbTransaction tx, ITable table, SyncTableRows rows, CancellationToken ct)
    {
        var columns = rows.Columns.Select(name => table.Columns.Single(c => c.Name == name)).ToList();
        var keys = SyncSchema.KeyColumns(table);
        var keyIndexes = keys.Select(k => Array.IndexOf(rows.Columns, k.Name)).ToList();
        var others = Enumerable.Range(0, columns.Count).Where(i => !keyIndexes.Contains(i)).ToList();
        var where = string.Join(" AND ", keys.Select((k, i) => $"{SyncSchema.Quote(k.Name)}=@k{i}"));
        var extra = table.Columns.Where(c => !SyncSchema.IsSyncedColumn(table.Name, c.Name) && SyncSchema.InsertDefault(table.Name, c) is not null).ToList();

        var count = 0;
        foreach (var row in rows.Upserts)
        {
            var values = columns.Select((c, i) => SyncValues.ToDb(row[i], c)).ToList();
            var keyValues = keyIndexes.Select(i => values[i]).ToList();
            var exists = await ScalarAsync(connection, tx, $"SELECT 1 FROM {SyncSchema.Quote(table.Name)} WHERE {where}", Named("k", keyValues), ct) is not null;
            if (exists)
            {
                if (others.Count > 0)
                {
                    var set = string.Join(",", others.Select(i => $"{SyncSchema.Quote(columns[i].Name)}=@v{i}"));
                    var parameters = Named("k", keyValues).Concat(others.Select(i => ($"@v{i}", values[i])));
                    await ExecuteAsync(connection, tx, $"UPDATE {SyncSchema.Quote(table.Name)} SET {set} WHERE {where}", parameters, ct);
                }
            }
            else
            {
                var names = columns.Select(c => c.Name).Concat(extra.Select(c => c.Name)).Select(SyncSchema.Quote);
                var allValues = values.Concat(extra.Select(c => SyncSchema.InsertDefault(table.Name, c))).ToList();
                await ExecuteAsync(connection, tx, $"INSERT INTO {SyncSchema.Quote(table.Name)} ({string.Join(",", names)}) VALUES ({string.Join(",", allValues.Select((_, i) => $"@v{i}"))})", Named("v", allValues), ct);
            }

            count++;
        }

        return count;
    }

    private static async Task<int> DeleteAsync(MySqlConnection connection, DbTransaction tx, ITable table, List<JsonElement[]> keys, CancellationToken ct)
    {
        var keyColumns = SyncSchema.KeyColumns(table);
        var where = string.Join(" AND ", keyColumns.Select((k, i) => $"{SyncSchema.Quote(k.Name)}=@k{i}"));
        var count = 0;
        foreach (var key in keys)
        {
            count += await ExecuteAsync(connection, tx, $"DELETE FROM {SyncSchema.Quote(table.Name)} WHERE {where}", Named("k", keyColumns.Select((c, i) => SyncValues.ToDb(key[i], c)).ToList()), ct);
        }

        return count;
    }

    private static IEnumerable<(string, object?)> Named(string prefix, IReadOnlyList<object?> values) =>
        values.Select((v, i) => ($"@{prefix}{i}", v));

    private static Task SetStateAsync(MySqlConnection connection, DbTransaction tx, string key, string value, CancellationToken ct) =>
        ExecuteAsync(connection, tx, "INSERT INTO `SyncStates` (`Key`,`Value`,`UpdatedAt`) VALUES (@k,@v,UTC_TIMESTAMP(6)) ON DUPLICATE KEY UPDATE `Value`=@v, `UpdatedAt`=UTC_TIMESTAMP(6)", [("@k", key), ("@v", value)], ct);

    private static async Task<int> ExecuteAsync(MySqlConnection connection, DbTransaction? tx, string sql, IEnumerable<(string Name, object? Value)> parameters, CancellationToken ct)
    {
        await using var command = Command(connection, tx, sql, parameters);
        return await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<object?> ScalarAsync(MySqlConnection connection, DbTransaction tx, string sql, IEnumerable<(string Name, object? Value)> parameters, CancellationToken ct)
    {
        await using var command = Command(connection, tx, sql, parameters);
        var value = await command.ExecuteScalarAsync(ct);
        return value is DBNull ? null : value;
    }

    private static MySqlCommand Command(MySqlConnection connection, DbTransaction? tx, string sql, IEnumerable<(string Name, object? Value)> parameters)
    {
        var command = new MySqlCommand(sql, connection, tx as MySqlTransaction);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }
}

/// <summary>Column values travel as JSON in the type the database stores, not the type the code uses.</summary>
public static class SyncValues
{
    public static JsonElement ToJson(object? value) =>
        value is null or DBNull ? JsonSerializer.SerializeToElement<object?>(null) : JsonSerializer.SerializeToElement(value, value.GetType(), JsonSerializerOptions.Web);

    public static object? ToDb(JsonElement value, IColumn column)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var type = Nullable.GetUnderlyingType(column.ProviderClrType) ?? column.ProviderClrType;
        if (type == typeof(bool) && value.ValueKind == JsonValueKind.Number)
        {
            return value.GetInt64() != 0;
        }

        return value.Deserialize(type, JsonSerializerOptions.Web);
    }
}
