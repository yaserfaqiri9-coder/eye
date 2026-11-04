using System.Net.Http.Json;
using System.Text.Json;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Finance;
using AlShifa.Api.Features.Users;
using AlShifa.Api.Features.Visits;
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
                // This fixture drops the database, so a mistaken value must never reach a real one.
                return DatabaseSafety.IsDisposable(fromEnv) ? fromEnv : throw new InvalidOperationException("ALSHIFA_TEST_DB must name a database ending in _test, _dev or _load.");
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

        using var scope = Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (var (code, price) in new[] { (ServiceCodes.VisitFee, 100m), (ServiceCodes.Refraction, 50m), (ServiceCodes.Imaging, 70m) })
        {
            var serviceId = await context.Services.Where(s => s.Code == code).Select(s => s.Id).SingleAsync();
            context.ServicePrices.Add(new ServicePrice { ServiceId = serviceId, Price = price, EffectiveFrom = DateTime.UtcNow.AddMinutes(-1) });
        }

        await context.SaveChangesAsync();
    }

    private readonly SemaphoreSlim deskLock = new(1, 1);
    private HttpClient? feeDesk;
    private HttpClient? feeCashier;

    /// <summary>A receptionist. Reception admits patients and takes no money.</summary>
    public async Task<HttpClient> FeeDeskAsync()
    {
        await deskLock.WaitAsync();
        try
        {
            if (feeDesk is null)
            {
                var name = $"desk_{Guid.NewGuid():N}"[..20];
                await CreateUserAsync(name, Roles.Receptionist);
                feeDesk = await LoginAsync(name);
            }

            return feeDesk;
        }
        finally
        {
            deskLock.Release();
        }
    }

    /// <summary>A cashier with an open cash session on a register of their own. Every fee is collected here.</summary>
    public async Task<HttpClient> FeeCashierAsync()
    {
        await deskLock.WaitAsync();
        try
        {
            if (feeCashier is null)
            {
                var name = $"cash_{Guid.NewGuid():N}"[..20];
                await CreateUserAsync(name, Roles.Cashier);
                var cashier = await LoginAsync(name);

                using var scope = Services.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var register = new CashRegister { Name = name };
                context.CashRegisters.Add(register);
                await context.SaveChangesAsync();

                var opened = await cashier.PostAsJsonAsync("/api/cash/sessions", new OpenSessionRequest(register.Id, 0));
                Assert.True(opened.IsSuccessStatusCode, await opened.Content.ReadAsStringAsync());
                feeCashier = cashier;
            }

            return feeCashier;
        }
        finally
        {
            deskLock.Release();
        }
    }

    /// <summary>The unpaid invoice of one patient's prepaid request, as the cashier's list would show it.</summary>
    public async Task<OrderFee> OrderFeeAsync(string fileNumber, string testName)
    {
        using var scope = Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await context.DiagnosticOrders.AsNoTracking()
            .Where(o => o.Patient!.FileNumber == fileNumber && o.Test!.Name == testName && o.Invoice != null && o.Invoice.Status != InvoiceStatus.Void && o.Invoice.Total - o.Invoice.Discount - o.Invoice.Paid > 0)
            .Select(o => new OrderFee(o.Invoice!.Id, o.Invoice.Total - o.Invoice.Discount - o.Invoice.Paid, o.Invoice.Version))
            .SingleAsync();
    }

    public async Task<JsonElement> PayFeeAsync(OrderFee fee)
    {
        var cashier = await FeeCashierAsync();
        var response = await cashier.SendAsync(Keyed("/api/payments", new PaymentRequest(fee.InvoiceId, fee.Balance, PaymentMethod.Cash, null, fee.InvoiceVersion)));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == System.Net.HttpStatusCode.Created, text);
        return JsonSerializer.Deserialize<JsonElement>(text);
    }

    public static HttpRequestMessage Keyed(string url, object body, string? key = null)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        message.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("N"));
        return message;
    }

    public async Task<int> DepartmentIdAsync(string code)
    {
        using var scope = Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Departments.Where(d => d.Code == code).Select(d => d.Id).SingleAsync();
    }

    public async Task<VisitTokenRow> NewTokenAsync(string department = "triage")
    {
        var desk = await FeeDeskAsync();
        var response = await desk.SendAsync(Keyed("/api/visit-tokens", new CreateVisitTokenRequest(await DepartmentIdAsync(department), null)));
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, text);
        return JsonSerializer.Deserialize<VisitTokenRow>(text, JsonSerializerOptions.Web)!;
    }

    /// <summary>What must happen before a visit can exist: a token from reception, with its visit fee fully paid at the cashier.</summary>
    public async Task<long> PaidTokenAsync(string department = "triage")
    {
        var token = await NewTokenAsync(department);
        var cashier = await FeeCashierAsync();
        var paid = await cashier.SendAsync(Keyed("/api/payments", new PaymentRequest(token.InvoiceId, token.Balance, PaymentMethod.Cash, null, token.InvoiceVersion)));
        Assert.True(paid.IsSuccessStatusCode, await paid.Content.ReadAsStringAsync());
        return token.Id;
    }

    private HttpClient? visionDesk;

    /// <summary>What Triage does before any doctor may start: Vision saved and completed.</summary>
    public async Task CompleteVisionAsync(long visitId)
    {
        if (visionDesk is null)
        {
            var name = $"vision_{Guid.NewGuid():N}"[..20];
            await CreateUserAsync(name, Roles.Triage);
            visionDesk = await LoginAsync(name);
        }

        var saved = await visionDesk.PutAsJsonAsync($"/api/pre-examinations/{visitId}", new AlShifa.Api.Features.Clinical.SavePreExamRequest(null, null, "6/9", "6/9", null, null, null, null, null));
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());

        var completed = await visionDesk.PostAsync($"/api/pre-examinations/{visitId}/complete", null);
        Assert.True(completed.IsSuccessStatusCode, await completed.Content.ReadAsStringAsync());
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

/// <summary>What the cashier needs to collect one prepaid request.</summary>
public sealed record OrderFee(long InvoiceId, decimal Balance, int InvoiceVersion);
