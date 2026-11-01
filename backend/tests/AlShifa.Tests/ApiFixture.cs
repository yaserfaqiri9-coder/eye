using System.Net.Http.Json;
using System.Text.Json;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Users;
using AlShifa.Api.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AlShifa.Tests;

/// <summary>Runs the real API against the real MySQL 8.4 test database, which is rebuilt once per test run.</summary>
public sealed class ApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string TestPassword = "Test-Password-123";

    private static string ConnectionString
    {
        get
        {
            var fromEnv = Environment.GetEnvironmentVariable("ALSHIFA_TEST_DB");
            if (!string.IsNullOrEmpty(fromEnv))
            {
                return fromEnv;
            }

            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, ".tools", "dev-secrets.json")))
            {
                dir = dir.Parent;
            }

            if (dir is null)
            {
                throw new InvalidOperationException("Set ALSHIFA_TEST_DB or run scripts/dev-db.ps1 first.");
            }

            using var secrets = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.FullName, ".tools", "dev-secrets.json")));
            var password = secrets.RootElement.GetProperty("app").GetString();
            return $"Server=127.0.0.1;Port=3307;Database=alshifa_test;User=alshifa;Password={password};";
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", ConnectionString);
        builder.UseSetting("RateLimit:LoginPerMinute", "100000");
        builder.UseSetting("Storage:Path", Path.Combine(Path.GetTempPath(), "alshifa-test-storage"));
        builder.ConfigureLogging(logging => logging.ClearProviders());
    }

    public async ValueTask InitializeAsync()
    {
        // Program.Main runs migrations and seeding outside Production, so the schema must be dropped before first use.
        await using (var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseMySQL(ConnectionString).Options))
        {
            await db.Database.EnsureDeletedAsync();
        }

        _ = Services;
    }

    Task IAsyncLifetime.InitializeAsync() => InitializeAsync().AsTask();

    Task IAsyncLifetime.DisposeAsync() => Task.CompletedTask;

    public async Task<long> CreateUserAsync(string userName, params string[] roles)
    {
        using var scope = Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = new AppUser { UserName = userName, FullName = userName };
        var result = await users.CreateAsync(user, TestPassword);
        Assert.True(result.Succeeded, string.Join(";", result.Errors.Select(e => e.Description)));
        await users.AddToRolesAsync(user, roles);
        return user.Id;
    }

    public async Task<HttpClient> LoginAsync(string userName, string password = TestPassword)
    {
        var client = NewClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(userName, password));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        await RefreshCsrfAsync(client);
        return client;
    }

    /// <summary>Client with a CSRF token but no session.</summary>
    public HttpClient NewClient()
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = true });
        RefreshCsrfAsync(client).GetAwaiter().GetResult();
        return client;
    }

    public static async Task RefreshCsrfAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/auth/csrf");
        response.EnsureSuccessStatusCode();
        var cookie = response.Headers.GetValues("Set-Cookie").First(c => c.StartsWith(AuthEndpoints.XsrfCookie + "="));
        var token = cookie.Split(';')[0][(AuthEndpoints.XsrfCookie.Length + 1)..];
        client.DefaultRequestHeaders.Remove("X-XSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-XSRF-TOKEN", Uri.UnescapeDataString(token));
    }
}

[CollectionDefinition("api")]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>;
