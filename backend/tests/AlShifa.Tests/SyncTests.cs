using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Patients;
using AlShifa.Api.Features.Settings;
using AlShifa.Api.Features.Sync;
using AlShifa.Api.Features.Users;
using AlShifa.Api.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MySql.Data.MySqlClient;

namespace AlShifa.Tests;

/// <summary>
/// A hospital server and an online copy, each with its own database. The online copy runs on a database account that may only read,
/// exactly as on the real server, so a write that slipped past the application would fail here too.
/// </summary>
public sealed class SyncFixture : IAsyncLifetime
{
    public const string Key = "test-sync-key-0123456789abcdef-0123456789";
    private const string ReadOnlyUser = "alshifa_mirror_ro";
    private readonly string readOnlyPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    public static string LocalDb => WithDatabase(ApiFixture.ConnectionString, "alshifa_synclocal_test");

    public static string MirrorDb => WithDatabase(ApiFixture.ConnectionString, "alshifa_mirror_test");

    public string MirrorReadOnlyDb => new MySqlConnectionStringBuilder(MirrorDb) { UserID = ReadOnlyUser, Password = readOnlyPassword }.ConnectionString;

    public WebApplicationFactory<Program> Mirror { get; private set; } = null!;

    public WebApplicationFactory<Program> Local { get; private set; } = null!;

    /// <summary>Simulates the hospital's internet: off throws before anything is sent, LoseAnswer applies the batch online but loses the reply.</summary>
    public FlakyNetwork Network { get; } = new();

    public async Task InitializeAsync()
    {
        foreach (var connection in new[] { LocalDb, MirrorDb })
        {
            await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseMySQL(connection).Options);
            await db.Database.EnsureDeletedAsync();
        }

        // The deployment migrates the online database with an owner account; the application then reads through its own.
        await using (var owner = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseMySQL(MirrorDb).Options))
        {
            await owner.Database.MigrateAsync();
        }

        await RootSqlAsync($"""
            DROP USER IF EXISTS '{ReadOnlyUser}'@'localhost';
            CREATE USER '{ReadOnlyUser}'@'localhost' IDENTIFIED BY '{readOnlyPassword}';
            GRANT SELECT ON alshifa_mirror_test.* TO '{ReadOnlyUser}'@'localhost';
            GRANT UPDATE (AccessFailedCount, LockoutEnd, ConcurrencyStamp) ON alshifa_mirror_test.AspNetUsers TO '{ReadOnlyUser}'@'localhost';
            """);

        Mirror = new App(MirrorReadOnlyDb, new()
        {
            ["Mirror:Enabled"] = "true",
            ["Mirror:SyncKey"] = Key,
            ["Mirror:SyncConnection"] = MirrorDb,
        });
        _ = Mirror.Server;

        Local = new App(LocalDb, new()
        {
            ["Sync:Url"] = "https://online.test",
            ["Sync:Key"] = Key,
            ["Sync:IntervalMinutes"] = "600",
        }, services => services.AddHttpClient(SyncWorker.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new FlakyHandler(Network, Mirror.Server.CreateHandler())));
        _ = Local.Server;
    }

    public async Task DisposeAsync()
    {
        await Local.DisposeAsync();
        await Mirror.DisposeAsync();
        await RootSqlAsync($"DROP USER IF EXISTS '{ReadOnlyUser}'@'localhost';");

        // Both databases exist only while the tests run.
        MySqlConnection.ClearAllPools();
        foreach (var connection in new[] { LocalDb, MirrorDb })
        {
            await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseMySQL(connection).Options);
            await db.Database.EnsureDeletedAsync();
        }
    }

    public Task SyncAsync() => Local.Services.GetRequiredService<SyncWorker>().RunOnceAsync(CancellationToken.None);

    public AppDbContext MirrorData() => new(new DbContextOptionsBuilder<AppDbContext>().UseMySQL(MirrorDb).Options);

    public async Task<HttpClient> LocalLoginAsync(string role)
    {
        var name = $"{role[..Math.Min(role.Length, 6)].ToLowerInvariant()}_{Guid.NewGuid():N}"[..20];
        await CreateUserAsync(Local, name, role);
        var client = Client(Local);
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(name, ApiFixture.TestPassword));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        await ApiFixture.RefreshCsrfAsync(client);
        return client;
    }

    public static async Task<string> CreateUserAsync(WebApplicationFactory<Program> app, string name, string role)
    {
        using var scope = app.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = new AppUser { UserName = name, FullName = name };
        Assert.True((await users.CreateAsync(user, ApiFixture.TestPassword)).Succeeded);
        await users.AddToRolesAsync(user, [role]);
        return name;
    }

    public static HttpClient Client(WebApplicationFactory<Program> app)
    {
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = true });
        ApiFixture.RefreshCsrfAsync(client).GetAwaiter().GetResult();
        return client;
    }

    /// <summary>The --online-access command, run as the operator would: on the online server, with the writing database account.</summary>
    public async Task<string> EnableOnlineAccessAsync(string userName, string password)
    {
        await using var operatorApp = new App(MirrorDb, new() { ["Mirror:Enabled"] = "true" });
        using var scope = operatorApp.Services.CreateScope();
        Environment.SetEnvironmentVariable("ALSHIFA_ONLINE_USER", userName);
        Environment.SetEnvironmentVariable("ALSHIFA_ONLINE_PASSWORD", password);
        try
        {
            await OnlineAccess.RunAsync(scope.ServiceProvider, revoke: false);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ALSHIFA_ONLINE_USER", null);
            Environment.SetEnvironmentVariable("ALSHIFA_ONLINE_PASSWORD", null);
        }

        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        return (await users.GetAuthenticatorKeyAsync((await users.FindByNameAsync(userName))!))!;
    }

    /// <summary>The hospital application on the same database with sync switched off, as after online.ps1 -Disable.</summary>
    public WebApplicationFactory<Program> LocalWithoutSync() => new App(LocalDb, []);

    private static string WithDatabase(string connection, string database) =>
        new MySqlConnectionStringBuilder(connection) { Database = database }.ConnectionString;

    private static async Task RootSqlAsync(string sql)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, ".tools", "dev-secrets.json")))
        {
            dir = dir.Parent;
        }

        using var secrets = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!.FullName, ".tools", "dev-secrets.json")));
        var root = new MySqlConnectionStringBuilder(ApiFixture.ConnectionString) { UserID = "root", Password = secrets.RootElement.GetProperty("root").GetString(), Database = "" };
        await using var connection = new MySqlConnection(root.ConnectionString);
        await connection.OpenAsync();
        foreach (var statement in sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            await using var command = new MySqlCommand(statement, connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    private sealed class App(string connection, Dictionary<string, string> settings, Action<IServiceCollection>? services = null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Default", connection);
            builder.UseSetting("RateLimit:LoginPerMinute", "100000");
            builder.UseSetting("Storage:Path", Path.Combine(Path.GetTempPath(), "alshifa-sync-test-" + (settings.ContainsKey("Mirror:Enabled") ? "online" : "local")));
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }

            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureTestServices(s =>
            {
                s.AddSingleton<IBackupTrigger>(new RecordingBackupTrigger());
                services?.Invoke(s);
            });
        }
    }
}

public sealed class FlakyNetwork
{
    public bool Offline { get; set; }

    public bool LoseAnswer { get; set; }
}

public sealed class FlakyHandler(FlakyNetwork network, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (network.Offline)
        {
            throw new HttpRequestException("No route to host", new System.Net.Sockets.SocketException());
        }

        var response = await base.SendAsync(request, ct);
        if (network.LoseAnswer)
        {
            throw new HttpRequestException("Connection reset while reading the answer");
        }

        return response;
    }
}

public sealed class SyncTests(SyncFixture sync) : IClassFixture<SyncFixture>
{
    [Fact]
    public async Task Patients_and_users_reach_the_online_copy_without_passwords()
    {
        var desk = await sync.LocalLoginAsync(Roles.Receptionist);
        var patient = await CreatePatientAsync(desk, "سینک");

        await sync.SyncAsync();

        await using var online = sync.MirrorData();
        var copied = await online.Patients.AsNoTracking().SingleAsync(p => p.Id == patient.Id);
        Assert.Equal(patient.FileNumber, copied.FileNumber);
        Assert.Equal("سینک", copied.FirstName);

        // Passwords and sign-in state never leave the hospital.
        Assert.True(await online.Users.AnyAsync());
        Assert.False(await online.Users.AnyAsync(u => u.PasswordHash != null));
        Assert.False(await online.Set<IdentityUserToken<long>>().AnyAsync());

        // Edits and deletions follow.
        var edited = await desk.PutAsJsonAsync($"/api/patients/{patient.Id}", new SavePatientRequest("سینک-۲", null, null, Gender.Female, null, 40, null, null, Version: patient.Version));
        Assert.True(edited.IsSuccessStatusCode, await edited.Content.ReadAsStringAsync());

        long departmentId;
        using (var scope = sync.Local.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var department = new AlShifa.Api.Features.Users.Department { Code = "SY" + Random.Shared.Next(1000, 9999), Name = "Sync test" };
            db.Departments.Add(department);
            await db.SaveChangesAsync();
            departmentId = department.Id;
        }

        await sync.SyncAsync();
        Assert.True(await online.Departments.AnyAsync(d => d.Id == departmentId));
        Assert.Equal("سینک-۲", (await online.Patients.AsNoTracking().SingleAsync(p => p.Id == patient.Id)).FirstName);

        using (var scope = sync.Local.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Departments.Remove(await db.Departments.SingleAsync(d => d.Id == departmentId));
            await db.SaveChangesAsync();
        }

        await sync.SyncAsync();
        Assert.False(await online.Departments.AnyAsync(d => d.Id == departmentId));
        await AssertNothingPendingAsync();
    }

    [Fact]
    public async Task Work_continues_offline_and_nothing_is_lost_or_doubled()
    {
        var desk = await sync.LocalLoginAsync(Roles.Receptionist);
        await sync.SyncAsync();

        // No internet: the hospital keeps working and the change waits.
        sync.Network.Offline = true;
        PatientDto patient;
        try
        {
            patient = await CreatePatientAsync(desk, "آفلاین");
            await Assert.ThrowsAsync<HttpRequestException>(sync.SyncAsync);

            var admin = await sync.LocalLoginAsync(Roles.SuperAdmin);
            var status = await admin.GetFromJsonAsync<SyncStatusDto>("/api/sync/status");
            Assert.True(status!.Pending > 0);
            Assert.False(status.Connected);
            Assert.NotNull(status.LastError);
        }
        finally
        {
            sync.Network.Offline = false;
        }

        // The batch arrives online but the reply is lost, so the hospital sends it again.
        sync.Network.LoseAnswer = true;
        try
        {
            await Assert.ThrowsAsync<HttpRequestException>(sync.SyncAsync);
        }
        finally
        {
            sync.Network.LoseAnswer = false;
        }

        await sync.SyncAsync();

        await using var online = sync.MirrorData();
        Assert.Equal(1, await online.Patients.CountAsync(p => p.FileNumber == patient.FileNumber));
        await AssertNothingPendingAsync();
    }

    [Fact]
    public async Task Online_copy_needs_two_step_sign_in_and_refuses_every_change()
    {
        var director = await SyncFixture.CreateUserAsync(sync.Local, $"dir_{Guid.NewGuid():N}"[..18], Roles.Manager);
        var desk = await sync.LocalLoginAsync(Roles.Receptionist);
        var patient = await CreatePatientAsync(desk, "مدیر");
        await sync.SyncAsync();

        // A synced account has no password online until the operator enables it.
        var anonymous = SyncFixture.Client(sync.Mirror);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest(director, ApiFixture.TestPassword))).StatusCode);

        const string onlinePassword = "Online-Password-2026";
        var key = await sync.EnableOnlineAccessAsync(director, onlinePassword);

        var withoutCode = await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest(director, onlinePassword));
        Assert.Equal(HttpStatusCode.Unauthorized, withoutCode.StatusCode);
        Assert.Contains("two_factor_required", await withoutCode.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest(director, onlinePassword, "000000"))).StatusCode);

        var client = SyncFixture.Client(sync.Mirror);
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(director, onlinePassword, Totp(key)));
        Assert.True(login.IsSuccessStatusCode, await login.Content.ReadAsStringAsync());
        var me = await login.Content.ReadFromJsonAsync<CurrentUserDto>();
        Assert.True(me!.ReadOnly);
        Assert.NotEmpty(me.Permissions);
        Assert.All(me.Permissions, p => Assert.EndsWith(".view", p));
        await ApiFixture.RefreshCsrfAsync(client);

        // Everything can be read.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/patients/{patient.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/reports/dashboard")).StatusCode);
        var status = await client.GetFromJsonAsync<SyncStatusDto>("/api/sync/status");
        Assert.Equal("mirror", status!.Mode);
        Assert.NotNull(status.LastSuccessAt);

        // Nothing can be changed, whatever the permission or the request.
        var create = await client.SendAsync(ApiFixture.Keyed("/api/patients", new SavePatientRequest("x", null, null, Gender.Male, null, 30, null, null)));
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        Assert.Contains("read_only", await create.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/patients/{patient.Id}", new SavePatientRequest("x", null, null, Gender.Male, null, 30, null, null, Version: patient.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"/api/patients/{patient.Id}?version={patient.Version}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/cash/registers", new SaveRegisterRequest("x"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/auth/change-password", new ChangePasswordRequest(onlinePassword, "Another-Password-1"))).StatusCode);

        // Even past the application, the online database account cannot write.
        await using var readOnly = new MySqlConnection(sync.MirrorReadOnlyDb);
        await readOnly.OpenAsync();
        await using var write = new MySqlCommand($"UPDATE Patients SET FirstName='x' WHERE Id={patient.Id}", readOnly);
        await Assert.ThrowsAsync<MySqlException>(() => write.ExecuteNonQueryAsync());

        // The receptionist was synced too, but was never enabled online.
        var deskName = (await sync.MirrorData().Users.AsNoTracking().Where(u => u.UserName!.StartsWith("recept")).Select(u => u.UserName).FirstAsync())!;
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest(deskName, ApiFixture.TestPassword))).StatusCode);
    }

    [Fact]
    public async Task Online_copy_accepts_only_signed_batches()
    {
        var client = SyncFixture.Client(sync.Mirror);
        var body = Encoding.UTF8.GetBytes("""{"schema":"x","seq":1,"sentAt":"2026-01-01T00:00:00Z","tables":[]}""");

        var unsigned = await client.PostAsync("/api/sync/mirror/apply", new ByteArrayContent(body));
        Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);

        var time = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var forged = new HttpRequestMessage(HttpMethod.Post, "/api/sync/mirror/apply") { Content = new ByteArrayContent(body) };
        forged.Headers.Add(SyncSignature.TimeHeader, time);
        forged.Headers.Add(SyncSignature.SignatureHeader, SyncSignature.Sign("another-key-0123456789abcdef-0123456789", time, "POST", "/api/sync/mirror/apply", body));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(forged)).StatusCode);

        var old = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds().ToString();
        var replayed = new HttpRequestMessage(HttpMethod.Post, "/api/sync/mirror/apply") { Content = new ByteArrayContent(body) };
        replayed.Headers.Add(SyncSignature.TimeHeader, old);
        replayed.Headers.Add(SyncSignature.SignatureHeader, SyncSignature.Sign(SyncFixture.Key, old, "POST", "/api/sync/mirror/apply", body));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(replayed)).StatusCode);

        // A correctly signed batch from another version is refused before anything is written.
        var other = new HttpRequestMessage(HttpMethod.Post, "/api/sync/mirror/apply") { Content = new ByteArrayContent(body) };
        other.Headers.Add(SyncSignature.TimeHeader, time);
        other.Headers.Add(SyncSignature.SignatureHeader, SyncSignature.Sign(SyncFixture.Key, time, "POST", "/api/sync/mirror/apply", body));
        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(other)).StatusCode);

        // The hospital server has no such endpoint at all.
        Assert.Equal(HttpStatusCode.NotFound, (await SyncFixture.Client(sync.Local).PostAsync("/api/sync/mirror/apply", new ByteArrayContent(body))).StatusCode);
    }

    [Fact]
    public async Task Online_copy_with_foreign_rows_or_lost_batches_is_rebuilt_from_the_hospital()
    {
        var desk = await sync.LocalLoginAsync(Roles.Receptionist);
        var patient = await CreatePatientAsync(desk, "بازسازی");
        await sync.SyncAsync();

        await using var online = sync.MirrorData();
        var accounts = await online.Users.CountAsync();

        // Rows the hospital never sent (a test run against the online copy, or a hospital database restored from a backup)
        // and a batch number this hospital never used.
        await online.Database.ExecuteSqlRawAsync("INSERT INTO Departments (Code, Name, IsActive, Version) VALUES ('ZZTEST', 'Test row', 1, 1)");
        await online.Database.ExecuteSqlRawAsync("UPDATE SyncStates SET `Value`='99999999999999' WHERE `Key`='apply.seq'");
        await sync.SyncAsync();
        Assert.False(await online.Departments.AnyAsync(d => d.Code == "ZZTEST"));
        Assert.True(await online.Patients.AnyAsync(p => p.Id == patient.Id));
        Assert.Equal(accounts, await online.Users.CountAsync());

        // The online copy loses data it had confirmed (restored from an older backup): everything is sent again.
        await online.Database.ExecuteSqlRawAsync($"DELETE FROM Patients WHERE Id={patient.Id}");
        await online.Database.ExecuteSqlRawAsync("UPDATE SyncStates SET `Value`='1' WHERE `Key`='apply.seq'");
        await sync.SyncAsync();
        Assert.Equal(1, await online.Patients.CountAsync(p => p.Id == patient.Id));
        await AssertNothingPendingAsync();
    }

    [Fact]
    public async Task Changes_made_while_sync_was_off_are_sent_when_it_is_on_again()
    {
        await sync.SyncAsync();

        long departmentId;
        await using (var off = sync.LocalWithoutSync())
        {
            using var scope = off.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            _ = off.Server;

            // The marker of the last full copy goes once the database answers.
            for (var i = 0; i < 50 && await db.SyncStates.AnyAsync(s => s.Key == "push.snapshotOf"); i++)
            {
                await Task.Delay(100);
            }

            Assert.False(await db.SyncStates.AnyAsync(s => s.Key == "push.snapshotOf"));
            var department = new AlShifa.Api.Features.Users.Department { Code = "OF" + Random.Shared.Next(1000, 9999), Name = "Made while sync was off" };
            db.Departments.Add(department);
            await db.SaveChangesAsync();
            departmentId = department.Id;
            Assert.False(await db.SyncChanges.AnyAsync());
        }

        await sync.SyncAsync();
        await using var online = sync.MirrorData();
        Assert.True(await online.Departments.AnyAsync(d => d.Id == departmentId));
    }

    [Fact]
    public async Task The_timed_sync_keeps_running_after_a_round_and_never_stops_the_application()
    {
        var desk = await sync.LocalLoginAsync(Roles.Receptionist);
        var patient = await CreatePatientAsync(desk, "زمان‌بندی");

        var services = sync.Local.Services;
        var worker = new SyncWorker(services.GetRequiredService<IServiceScopeFactory>(), services.GetRequiredService<IHttpClientFactory>(),
            Microsoft.Extensions.Options.Options.Create(new SyncOptions { Url = "https://online.test", Key = SyncFixture.Key, IntervalMinutes = 1, StartDelaySeconds = 0 }),
            services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(), services.GetRequiredService<Microsoft.Extensions.Hosting.IHostEnvironment>(),
            services.GetRequiredService<ILogger<SyncWorker>>());
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await using var online = sync.MirrorData();
            for (var i = 0; i < 100 && !await online.Patients.AnyAsync(p => p.Id == patient.Id); i++)
            {
                await Task.Delay(100);
            }

            Assert.True(await online.Patients.AnyAsync(p => p.Id == patient.Id));

            // After the round it waits for the next one instead of failing.
            await Task.Delay(1000);
            Assert.False(worker.ExecuteTask!.IsCompleted, worker.ExecuteTask.Exception?.ToString());
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        Assert.Equal(Microsoft.Extensions.Hosting.BackgroundServiceExceptionBehavior.Ignore,
            services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.Extensions.Hosting.HostOptions>>().Value.BackgroundServiceExceptionBehavior);
    }

    private async Task AssertNothingPendingAsync()
    {
        using var scope = sync.Local.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<AppDbContext>().SyncChanges.CountAsync());
    }

    private static async Task<PatientDto> CreatePatientAsync(HttpClient desk, string name)
    {
        var response = await desk.SendAsync(ApiFixture.Keyed("/api/patients", new SavePatientRequest(name, null, null, Gender.Male, null, 30, null, null, ConfirmNotDuplicate: true)));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<PatientDto>())!;
    }

    /// <summary>RFC 6238, as an authenticator app computes it.</summary>
    private static string Totp(string base32Key)
    {
        var key = Base32(base32Key);
        var counter = BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(counter);
        }

        var hash = HMACSHA1.HashData(key, counter);
        var offset = hash[^1] & 0x0f;
        var code = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (code % 1_000_000).ToString("D6");
    }

    private static byte[] Base32(string text)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = string.Concat(text.TrimEnd('=').ToUpperInvariant().Select(c => Convert.ToString(alphabet.IndexOf(c), 2).PadLeft(5, '0')));
        return Enumerable.Range(0, bits.Length / 8).Select(i => Convert.ToByte(bits.Substring(i * 8, 8), 2)).ToArray();
    }
}
