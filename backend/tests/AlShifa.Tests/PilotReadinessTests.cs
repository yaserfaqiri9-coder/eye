using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Clinical;
using AlShifa.Api.Features.Finance;
using AlShifa.Api.Features.Inventory;
using AlShifa.Api.Features.Patients;
using AlShifa.Api.Features.Settings;
using AlShifa.Api.Features.Users;
using AlShifa.Api.Features.Visits;
using AlShifa.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AlShifa.Tests;

/// <summary>Scenarios for the four-user hospital pilot that no other test class covers end to end.</summary>
[Collection("api")]
public sealed class PilotReadinessTests(ApiFixture api)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private static string Name(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..20];

    private async Task<HttpClient> UserAsync(params string[] roles)
    {
        var name = Name("u");
        await api.CreateUserAsync(name, roles);
        return await api.LoginAsync(name);
    }

    private static HttpRequestMessage Post(string url, object body, string? key = null)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        message.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("N"));
        return message;
    }

    private static async Task<T> Created<T>(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, text);
        return JsonSerializer.Deserialize<T>(text, JsonOptions)!;
    }

    private async Task<int> NewServiceAsync(decimal price)
    {
        var admin = await UserAsync(Roles.SuperAdmin);
        var response = await admin.PostAsJsonAsync("/api/services", new SaveServiceRequest(Name("S"), "خدمت " + Name("n"), null, true, price));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt32();
    }

    private async Task<HttpClient> CashierWithOpenSessionAsync()
    {
        var cashier = await UserAsync(Roles.Cashier);
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var register = new CashRegister { Name = Name("صندوق") };
        db.CashRegisters.Add(register);
        await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Created, (await cashier.PostAsJsonAsync("/api/cash/sessions", new OpenSessionRequest(register.Id, 0))).StatusCode);
        return cashier;
    }

    private async Task AssertLedgerReconciles()
    {
        using var scope = api.Services.CreateScope();
        var result = await FinanceEndpoints.ReconcileAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), default);
        Assert.True(result.Balanced, JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task Four_users_log_in_together_with_independent_sessions()
    {
        var people = new[] { (Name("rec"), Roles.Receptionist), (Name("doc"), Roles.Doctor), (Name("pha"), Roles.Pharmacist), (Name("cas"), Roles.Cashier) };
        foreach (var (userName, role) in people)
        {
            await api.CreateUserAsync(userName, role);
        }

        var clients = await Task.WhenAll(people.Select(p => api.LoginAsync(p.Item1)));
        var who = await Task.WhenAll(clients.Select(c => c.GetFromJsonAsync<CurrentUserDto>("/api/auth/me")));
        Assert.Equal(people.Select(p => p.Item1), who.Select(u => u!.UserName));
        Assert.Equal(people.Select(p => p.Item2), who.Select(u => Assert.Single(u!.Roles)));

        // Ending one session must leave the other three untouched.
        Assert.Equal(HttpStatusCode.NoContent, (await clients[0].PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await clients[0].GetAsync("/api/auth/me")).StatusCode);
        foreach (var client in clients.Skip(1))
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
        }
    }

    [Fact]
    public async Task Failure_halfway_through_an_invoice_leaves_nothing_behind_and_the_retry_succeeds()
    {
        var serviceId = await NewServiceAsync(250);
        var cashier = await UserAsync(Roles.Cashier);
        var key = Guid.NewGuid().ToString("N");

        async Task<(int Invoices, int Items, int Ledger, long Numbers)> CountAsync()
        {
            using var scope = api.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return (await db.Invoices.CountAsync(), await db.InvoiceItems.CountAsync(), await db.LedgerTransactions.CountAsync(), await db.NumberSequences.SumAsync(n => (long)n.Value));
        }

        var before = await CountAsync();

        // The first line is written before the second one fails.
        var failed = await cashier.SendAsync(Post("/api/invoices", new CreateInvoiceRequest(null, null, [new InvoiceLineInput(serviceId, 1), new InvoiceLineInput(int.MaxValue, 1)]), key));
        Assert.Equal(HttpStatusCode.BadRequest, failed.StatusCode);
        Assert.Equal(before, await CountAsync());

        // A failed attempt must not use up the key, or the user could never complete the action.
        var retried = await Created<InvoiceRow>(await cashier.SendAsync(Post("/api/invoices", new CreateInvoiceRequest(null, null, [new InvoiceLineInput(serviceId, 1)]), key)));
        Assert.Equal(250, retried.Balance);
        await AssertLedgerReconciles();
    }

    [Fact]
    public async Task Failure_on_the_last_line_of_a_sale_returns_stock_taken_by_earlier_lines()
    {
        var manager = await UserAsync(Roles.Inventory);
        async Task<int> ProductAsync(decimal quantity)
        {
            var response = await manager.PostAsJsonAsync("/api/inventory/products", new SaveProductRequest(Name("D"), "جنس " + Name("n"), ProductKind.Drug, null, "عدد", 0, 100));
            var id = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt32();
            Assert.Equal(HttpStatusCode.Created, (await manager.SendAsync(Post("/api/inventory/purchases", new CreatePurchaseRequest("تأمین‌کننده", "pharmacy",
                [new PurchaseLineInput(id, Name("L"), DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)), quantity, 40)])))).StatusCode);
            return id;
        }

        var plenty = await ProductAsync(10);
        var scarce = await ProductAsync(1);
        var pharmacist = await UserAsync(Roles.Pharmacist);

        var failed = await pharmacist.SendAsync(Post("/api/pharmacy/sales", new CreateSaleRequest(null, null, [new SaleLineInput(plenty, 3, null), new SaleLineInput(scarce, 2, null)])));
        Assert.Equal(HttpStatusCode.Conflict, failed.StatusCode);

        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(10, await db.StockBatches.Where(b => b.ProductId == plenty).SumAsync(b => b.Quantity));
        Assert.Equal(1, await db.StockBatches.Where(b => b.ProductId == scarce).SumAsync(b => b.Quantity));
        await AssertLedgerReconciles();
    }

    [Fact]
    public async Task Every_api_endpoint_requires_authorization_except_login_and_csrf()
    {
        var open = api.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api", StringComparison.Ordinal) == true)
            .Where(e => e.Metadata.GetMetadata<IAuthorizeData>() is null)
            .Select(e => e.RoutePattern.RawText)
            .Distinct()
            .Order()
            .ToList();

        Assert.Equal(["/api/auth/csrf", "/api/auth/login"], open);
    }

    [Fact]
    public async Task Departments_can_run_the_whole_patient_journey_at_the_same_time()
    {
        var serviceId = await NewServiceAsync(400);
        var reception = await UserAsync(Roles.Receptionist);
        var doctor = await UserAsync(Roles.Doctor);
        var pharmacist = await UserAsync(Roles.Pharmacist);
        var cashier = await CashierWithOpenSessionAsync();
        var departments = await reception.GetFromJsonAsync<List<LookupItem>>("/api/lookups/departments");
        var doctorDepartment = (int)departments!.Single(d => d.Name == "داکتر").Id;

        async Task<(string Receipt, string Rx)> JourneyAsync(int number)
        {
            // Same name on purpose: patients who share a name are different people unless a user says otherwise.
            var patient = await Created<PatientDto>(await reception.SendAsync(Post("/api/patients",
                new SavePatientRequest("احمد", null, null, Gender.Male, null, 20 + number, null, null))));
            var visit = await Created<VisitRow>(await reception.SendAsync(Post("/api/visits", new CreateVisitRequest(patient.Id, doctorDepartment, null, null, serviceId, await api.PaidTokenAsync()))));

            var seen = await doctor.GetFromJsonAsync<PatientDto>($"/api/patients/{patient.Id}", JsonOptions);
            Assert.Equal(patient.FileNumber, seen!.FileNumber);

            var rx = await Created<PrescriptionDto>(await doctor.SendAsync(Post("/api/prescriptions",
                new CreatePrescriptionRequest(visit.Id, null, [new RxItemInput($"دوا-{number}", null, null, null, null, 1)]))));
            Assert.Contains(rx.RxNumber, await pharmacist.GetStringAsync("/api/prescriptions?status=Issued"));

            var unpaid = await cashier.GetFromJsonAsync<PagedResult<InvoiceRow>>($"/api/invoices?unpaid=true&q={patient.FileNumber}", JsonOptions);
            var invoice = Assert.Single(unpaid!.Items);
            var payment = await Created<PaymentRow>(await cashier.SendAsync(Post("/api/payments", new PaymentRequest(invoice.Id, invoice.Balance, PaymentMethod.Cash, null))));

            var paid = await cashier.GetFromJsonAsync<InvoiceDetail>($"/api/invoices/{invoice.Id}", JsonOptions);
            Assert.Equal(0, paid!.Invoice.Balance);
            return (payment.ReceiptNumber, rx.RxNumber);
        }

        var results = await Task.WhenAll(Enumerable.Range(1, 12).Select(JourneyAsync));

        Assert.Equal(12, results.Select(r => r.Receipt).Distinct().Count());
        Assert.Equal(12, results.Select(r => r.Rx).Distinct().Count());
        await AssertLedgerReconciles();
    }

    [Fact]
    public async Task Backup_status_warns_when_a_backup_cannot_be_confirmed()
    {
        var now = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);
        var file = Path.Combine(Path.GetTempPath(), $"alshifa-backup-status-{Guid.NewGuid():N}.json");
        try
        {
            Assert.False(BackupStatus.Read(null, now).NeedsAttention);
            Assert.True(BackupStatus.Read(file, now).NeedsAttention);

            await File.WriteAllTextAsync(file, """{ "lastSuccessAt": "2026-11-02T08:00:00Z", "lastError": null }""");
            Assert.False(BackupStatus.Read(file, now).NeedsAttention);
            Assert.True(BackupStatus.Read(file, now.AddHours(25)).NeedsAttention);

            await File.WriteAllTextAsync(file, """{ "lastSuccessAt": "2026-11-02T08:00:00Z", "lastError": "disk missing" }""");
            Assert.True(BackupStatus.Read(file, now).NeedsAttention);

            await File.WriteAllTextAsync(file, "not json");
            Assert.True(BackupStatus.Read(file, now).NeedsAttention);
        }
        finally
        {
            File.Delete(file);
        }

        var reception = await UserAsync(Roles.Receptionist);
        Assert.Equal(HttpStatusCode.Forbidden, (await reception.GetAsync("/api/settings/backup-status")).StatusCode);
        var manager = await UserAsync(Roles.Manager);
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync("/api/settings/backup-status")).StatusCode);
    }

    [Fact]
    public async Task Api_serves_the_web_app_and_unknown_api_paths_stay_404()
    {
        var webRoot = Directory.CreateTempSubdirectory("alshifa-webroot");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(webRoot.FullName, "index.html"), "<!doctype html><title>app</title>");
            Directory.CreateDirectory(Path.Combine(webRoot.FullName, "assets"));
            await File.WriteAllTextAsync(Path.Combine(webRoot.FullName, "assets", "a-123.js"), "1");

            await using var hosting = api.WithWebHostBuilder(b => b.UseSetting(WebHostDefaults.WebRootKey, webRoot.FullName));
            var client = hosting.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

            var page = await client.GetAsync("/patients/15");
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Contains("<title>app</title>", await page.Content.ReadAsStringAsync());
            Assert.Equal("no-cache", page.Headers.CacheControl!.ToString());
            Assert.Contains("default-src 'self'", page.Headers.GetValues("Content-Security-Policy").Single());

            var asset = await client.GetAsync("/assets/a-123.js");
            Assert.True(asset.Headers.CacheControl!.Public);

            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/not-a-real-endpoint")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/assets/gone-456.js")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/patients")).StatusCode);
        }
        finally
        {
            webRoot.Delete(true);
        }
    }
}
