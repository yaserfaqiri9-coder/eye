using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AlShifa.Tests;

/// <summary>Online report access through Cloudflare Tunnel and Access. Tokens are signed here with a test key in place of Cloudflare's.</summary>
[Collection("api")]
public sealed class RemoteAccessTests(ApiFixture api) : IDisposable
{
    private const int RemotePort = 5081;
    private const string Team = "alshifa-test.cloudflareaccess.com";
    private const string Audience = "test-aud-tag";

    private readonly RSA cloudflareKey = RSA.Create(2048);
    private readonly RSA otherKey = RSA.Create(2048);

    public void Dispose()
    {
        cloudflareKey.Dispose();
        otherKey.Dispose();
    }

    private sealed class FixedKeys(RSA key) : IAccessKeySource
    {
        public Task<IReadOnlyDictionary<string, RSA>> GetKeysAsync(bool refresh, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, RSA>>(new Dictionary<string, RSA> { ["k1"] = key });
    }

    /// <summary>Makes every request look as if cloudflared had connected to the remote loopback port.</summary>
    private sealed class ThroughTunnel : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((http, nextMiddleware) =>
            {
                http.Connection.LocalIpAddress = IPAddress.Loopback;
                http.Connection.LocalPort = RemotePort;
                return nextMiddleware();
            });
            next(app);
        };
    }

    private WebApplicationFactory<Program> Tunnel() => api.WithWebHostBuilder(b =>
    {
        b.UseSetting("RemoteAccess:Port", RemotePort.ToString());
        b.UseSetting("RemoteAccess:TeamDomain", Team);
        b.UseSetting("RemoteAccess:Audience", Audience);
        b.ConfigureTestServices(s =>
        {
            s.AddSingleton<IAccessKeySource>(new FixedKeys(cloudflareKey));
            s.AddSingleton<IStartupFilter, ThroughTunnel>();
        });
    });

    private static string Encode(object value) => Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(value));

    private string Token(string email = "doctor@example.org", string audience = Audience, string issuer = $"https://{Team}", int expiresInMinutes = 30, RSA? key = null, string alg = "RS256")
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var unsigned = $"{Encode(new { alg, kid = "k1", typ = "JWT" })}.{Encode(new { aud = new[] { audience }, email, iss = issuer, iat = now, nbf = now, exp = now + (expiresInMinutes * 60), type = "app" })}";
        var signature = (key ?? cloudflareKey).SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{unsigned}.{Base64Url.EncodeToString(signature)}";
    }

    private static HttpClient Client(WebApplicationFactory<Program> host, string? token)
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = true });
        if (token is not null)
        {
            client.DefaultRequestHeaders.Add(RemoteAccess.TokenHeader, token);
        }

        return client;
    }

    private async Task<string> AccountAsync(params string[] roles)
    {
        var name = $"r_{Guid.NewGuid():N}"[..20];
        await api.CreateUserAsync(name, roles);
        return name;
    }

    private static async Task<HttpResponseMessage> LoginAsync(HttpClient client, string userName)
    {
        await ApiFixture.RefreshCsrfAsync(client);
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(userName, ApiFixture.TestPassword));
        if (response.IsSuccessStatusCode)
        {
            await ApiFixture.RefreshCsrfAsync(client);
        }

        return response;
    }

    [Fact]
    public async Task Requests_without_a_valid_Access_token_never_reach_the_application()
    {
        await using var host = Tunnel();
        var badTokens = new[]
        {
            null,
            "not-a-token",
            Token(audience: "another-application"),
            Token(issuer: "https://someone-else.cloudflareaccess.com"),
            Token(expiresInMinutes: -10),
            Token(key: otherKey),
            Token(alg: "HS256"),
            Token(email: ""),
        };

        foreach (var token in badTokens)
        {
            var client = Client(host, token);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/auth/csrf")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/reports/overview")).StatusCode);
        }
    }

    [Fact]
    public async Task Report_viewer_reads_reports_online_and_reaches_nothing_else()
    {
        var viewer = await AccountAsync(Roles.DoctorReportsViewer);
        await using var host = Tunnel();
        var client = Client(host, Token());

        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/reports/dashboard")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/reports/overview")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/reports/diseases")).StatusCode);

        // Money, stock, patient records, prints, attachments and every change are outside the online path.
        foreach (var path in new[] { "/api/reports/income", "/api/reports/stock", "/api/reports/sales", "/api/patients", "/api/print/patient/1", "/api/attachments/1", "/api/users", "/health" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/patients", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync("/api/settings", new { })).StatusCode);

        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var login = await db.AuditLogs.AsNoTracking().Where(a => a.Action == "Login").OrderByDescending(a => a.Id).FirstAsync();
        Assert.Contains("doctor@example.org", login.Details);
    }

    [Fact]
    public async Task Only_dedicated_report_viewer_accounts_may_sign_in_online()
    {
        var manager = await AccountAsync(Roles.Manager);
        var mixed = await AccountAsync(Roles.DoctorReportsViewer, Roles.Doctor);
        await using var host = Tunnel();

        foreach (var account in new[] { manager, mixed })
        {
            var client = Client(host, Token());
            var response = await LoginAsync(client, account);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        }

        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.True(await db.AuditLogs.AnyAsync(a => a.Action == "RemoteLoginDenied"));
    }

    [Fact]
    public async Task Report_viewer_is_read_only_inside_the_hospital_too()
    {
        var viewer = await api.LoginAsync(await AccountAsync(Roles.DoctorReportsViewer));

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/reports/overview")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/reports/income")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/patients")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/patients", new { })).StatusCode);
    }

    [Fact]
    public async Task Hospital_network_does_not_need_Cloudflare()
    {
        // Online access configured, but the request did not come through the tunnel port: no token, no Cloudflare, works as before.
        await using var host = api.WithWebHostBuilder(b =>
        {
            b.UseSetting("RemoteAccess:Port", RemotePort.ToString());
            b.UseSetting("RemoteAccess:TeamDomain", Team);
            b.UseSetting("RemoteAccess:Audience", Audience);
        });
        var reception = await AccountAsync(Roles.Receptionist);
        var client = Client(host, token: null);

        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, reception)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/patients?q=test")).StatusCode);
    }
}
