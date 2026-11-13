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
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AlShifa.Tests;

/// <summary>Scenarios for several departments working at once on one host. All data is synthetic.</summary>
[Collection("api")]
public sealed class HospitalReadinessTests(ApiFixture api)
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
        var response = await admin.PostAsJsonAsync("/api/services", new SaveServiceRequest("خدمت " + Name("n"), null, true, price));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt32();
    }

    private async Task<int> NewProductAsync(decimal quantity)
    {
        var inventory = await UserAsync(Roles.Inventory);
        var created = await inventory.PostAsJsonAsync("/api/inventory/products", new SaveProductRequest("جنس " + Name("n"), ProductKind.Drug, null, "عدد", 0, 100));
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var productId = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt32();

        if (quantity > 0)
        {
            var purchase = await inventory.SendAsync(Post("/api/inventory/purchases", new CreatePurchaseRequest("تأمین‌کننده", "pharmacy",
                [new PurchaseLineInput(productId, Name("L"), DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)), quantity, 40)])));
            Assert.True(purchase.StatusCode == HttpStatusCode.Created, await purchase.Content.ReadAsStringAsync());
        }

        return productId;
    }

    private async Task<HttpClient> CashierWithOpenSessionAsync()
    {
        var cashier = await UserAsync(Roles.Cashier);
        int registerId;
        using (var scope = api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var register = new CashRegister { Name = Name("صندوق") };
            db.CashRegisters.Add(register);
            await db.SaveChangesAsync();
            registerId = register.Id;
        }

        var opened = await cashier.PostAsJsonAsync("/api/cash/sessions", new OpenSessionRequest(registerId, 0));
        Assert.True(opened.StatusCode == HttpStatusCode.Created, await opened.Content.ReadAsStringAsync());
        return cashier;
    }

    private async Task AssertLedgerReconciles()
    {
        using var scope = api.Services.CreateScope();
        var result = await FinanceEndpoints.ReconcileAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), default);
        Assert.True(result.Balanced, JsonSerializer.Serialize(result));
    }

    private static SavePatientRequest NewPatient() => new(Name("مریض"), null, null, Gender.Male, null, 30, null, null);

    private static async Task<int> DepartmentAsync(HttpClient client, string name) =>
        (int)(await client.GetFromJsonAsync<List<LookupItem>>("/api/lookups/departments"))!.Single(d => d.Name == name).Id;

    [Fact]
    public async Task Four_users_log_in_at_once_and_their_sessions_stay_independent()
    {
        var roles = new[] { Roles.Receptionist, Roles.Doctor, Roles.Pharmacist, Roles.Cashier };
        var names = roles.Select(_ => Name("u")).ToArray();
        for (var i = 0; i < roles.Length; i++)
        {
            await api.CreateUserAsync(names[i], roles[i]);
        }

        var clients = await Task.WhenAll(names.Select(n => api.LoginAsync(n)));

        var me = await Task.WhenAll(clients.Select(c => c.GetFromJsonAsync<CurrentUserDto>("/api/auth/me")));
        Assert.Equal(names, me.Select(m => m!.UserName));
        Assert.Equal(roles, me.Select(m => Assert.Single(m!.Roles)));

        Assert.Equal(HttpStatusCode.NoContent, (await clients[0].PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await clients[0].GetAsync("/api/auth/me")).StatusCode);
        foreach (var other in clients.Skip(1))
        {
            Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/api/auth/me")).StatusCode);
        }
    }

    [Fact]
    public async Task Patient_registered_at_reception_is_the_same_record_for_the_doctor()
    {
        var reception = await UserAsync(Roles.Receptionist);
        var patient = await Created<PatientDto>(await reception.SendAsync(Post("/api/patients", NewPatient())));
        var visit = await Created<VisitRow>(await reception.SendAsync(Post("/api/visits", new CreateVisitRequest(patient.Id, await DepartmentAsync(reception, "داکتر"), null, null, null, await api.PaidTokenAsync()))));

        await api.CompleteVisionAsync(visit.Id);
        var doctor = await UserAsync(Roles.Doctor);
        var seen = await doctor.GetFromJsonAsync<PatientDto>($"/api/patients/{patient.Id}", JsonOptions);
        Assert.Equal(patient, seen);

        var worklist = await doctor.GetFromJsonAsync<List<VisitRow>>("/api/clinical/worklist?kind=doctor", JsonOptions);
        Assert.Contains(worklist!, v => v.Id == visit.Id && v.PatientId == patient.Id);
    }

    [Fact]
    public async Task Prescription_written_by_the_doctor_reaches_pharmacy_unchanged()
    {
        var reception = await UserAsync(Roles.Receptionist);
        var patient = await Created<PatientDto>(await reception.SendAsync(Post("/api/patients", NewPatient())));
        var visit = await Created<VisitRow>(await reception.SendAsync(Post("/api/visits", new CreateVisitRequest(patient.Id, await DepartmentAsync(reception, "داکتر"), null, null, null, await api.PaidTokenAsync()))));

        var doctor = await UserAsync(Roles.Doctor);
        var rx = await Created<PrescriptionDto>(await doctor.SendAsync(Post("/api/prescriptions",
            new CreatePrescriptionRequest(visit.Id, null, [new RxItemInput("قطره چشم", "روزانه ۲ بار", null, "۷ روز", null, 2)]))));

        var pharmacist = await UserAsync(Roles.Pharmacist);
        var seen = Assert.Single((await pharmacist.GetFromJsonAsync<List<PrescriptionDto>>("/api/prescriptions?q=" + rx.RxNumber, JsonOptions))!);
        Assert.Equal(patient.Id, seen.PatientId);
        var item = Assert.Single(seen.Items);
        Assert.Equal(("قطره چشم", "روزانه ۲ بار", "۷ روز", 2m), (item.DrugName, item.Dose, item.Duration, item.Quantity));
    }

    [Fact]
    public async Task Conflicting_edits_of_one_record_cannot_silently_overwrite_each_other()
    {
        var reception = await UserAsync(Roles.Receptionist);
        var other = await UserAsync(Roles.Receptionist);
        var patient = await Created<PatientDto>(await reception.SendAsync(Post("/api/patients", NewPatient())));

        var responses = await Task.WhenAll(
            reception.PutAsJsonAsync($"/api/patients/{patient.Id}", NewPatient() with { FirstName = "اول", Version = patient.Version }),
            other.PutAsJsonAsync($"/api/patients/{patient.Id}", NewPatient() with { FirstName = "دوم", Version = patient.Version }));

        var won = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        var lost = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Contains("concurrency_conflict", await lost.Content.ReadAsStringAsync());

        var winner = await won.Content.ReadFromJsonAsync<PatientDto>(JsonOptions);
        var stored = await reception.GetFromJsonAsync<PatientDto>($"/api/patients/{patient.Id}", JsonOptions);
        Assert.Equal(winner!.FirstName, stored!.FirstName);
        Assert.Equal(patient.Version + 1, stored.Version);
    }

    [Fact]
    public async Task Same_logical_payment_from_two_screens_with_different_keys_is_taken_once()
    {
        var cashierForInvoice = await UserAsync(Roles.Cashier);
        var reception = await UserAsync(Roles.Receptionist);
        var patient = await Created<PatientDto>(await reception.SendAsync(Post("/api/patients", NewPatient())));
        var invoice = await Created<InvoiceRow>(await cashierForInvoice.SendAsync(Post("/api/invoices",
            new CreateInvoiceRequest(patient.Id, null, [new InvoiceLineInput(await NewServiceAsync(1000), 1)]))));

        var first = await CashierWithOpenSessionAsync();
        var second = await CashierWithOpenSessionAsync();

        // The amount fits the balance twice, so only the version on screen can tell this is a repeat.
        var responses = await Task.WhenAll(
            first.SendAsync(Post("/api/payments", new PaymentRequest(invoice.Id, 300, PaymentMethod.Cash, null, invoice.Version))),
            second.SendAsync(Post("/api/payments", new PaymentRequest(invoice.Id, 300, PaymentMethod.Cash, null, invoice.Version))));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        var refused = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Contains("invoice_changed", await refused.Content.ReadAsStringAsync());

        var detail = await first.GetFromJsonAsync<InvoiceDetail>($"/api/invoices/{invoice.Id}", JsonOptions);
        Assert.Single(detail!.Payments);
        Assert.Equal(700, detail.Invoice.Balance);
        await AssertLedgerReconciles();
    }

    [Fact]
    public async Task Concurrent_refunds_of_one_payment_never_lose_an_amount_or_exceed_it()
    {
        var cashier = await CashierWithOpenSessionAsync();
        var reception = await UserAsync(Roles.Receptionist);
        var patient = await Created<PatientDto>(await reception.SendAsync(Post("/api/patients", NewPatient())));
        var invoice = await Created<InvoiceRow>(await cashier.SendAsync(Post("/api/invoices",
            new CreateInvoiceRequest(patient.Id, null, [new InvoiceLineInput(await NewServiceAsync(1000), 1)]))));
        var payment = await Created<PaymentRow>(await cashier.SendAsync(Post("/api/payments", new PaymentRequest(invoice.Id, 1000, PaymentMethod.Cash, null))));

        var accountant = await UserAsync(Roles.Accountant);
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            accountant.SendAsync(Post("/api/refunds", new RefundRequest(payment.Id, 200, $"برگشت {i}")))));
        var accepted = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        Assert.True(accepted <= 5);

        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == payment.Id);
        var refunds = await db.Refunds.AsNoTracking().Where(r => r.PaymentId == payment.Id).SumAsync(r => r.Amount);
        var total = await db.Invoices.AsNoTracking().Where(i => i.Id == invoice.Id).Select(i => i.Refunded).SingleAsync();

        Assert.Equal(accepted * 200, refunds);
        Assert.Equal(refunds, stored.Refunded);
        Assert.Equal(refunds, total);
        await AssertLedgerReconciles();
    }

    [Fact]
    public async Task Concurrent_dispensing_of_one_prescription_counts_every_unit()
    {
        var productId = await NewProductAsync(100);
        var reception = await UserAsync(Roles.Receptionist);
        var patient = await Created<PatientDto>(await reception.SendAsync(Post("/api/patients", NewPatient())));
        var visit = await Created<VisitRow>(await reception.SendAsync(Post("/api/visits", new CreateVisitRequest(patient.Id, await DepartmentAsync(reception, "داکتر"), null, null, null, await api.PaidTokenAsync()))));

        // Two items, one already started: the status stays PartiallyDispensed, so no version check catches a lost update.
        var doctor = await UserAsync(Roles.Doctor);
        var drug = await api.InDbAsync(db => db.Products.Where(p => p.Id == productId).Select(p => p.Name).SingleAsync());
        var rx = await Created<PrescriptionDto>(await doctor.SendAsync(Post("/api/prescriptions",
            new CreatePrescriptionRequest(visit.Id, null, [new RxItemInput(drug, "یک قطره", null, null, null, 50), new RxItemInput(drug, "دو قطره", null, null, null, 50)]))));

        var pharmacist = await UserAsync(Roles.Pharmacist);
        var started = await Created<SaleRow>(await pharmacist.SendAsync(Post("/api/pharmacy/sales", new CreateSaleRequest(patient.Id, rx.Id, [new SaleLineInput(productId, 1, rx.Items[1].Id)]))));
        await api.PayAndDeliverAsync(pharmacist, started.Id);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            pharmacist.SendAsync(Post("/api/pharmacy/sales", new CreateSaleRequest(patient.Id, rx.Id, [new SaleLineInput(productId, 1, rx.Items[0].Id)])))));
        var sold = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        Assert.True(sold > 0);

        // The paid bills are handed over at the same moment too.
        var bills = await Task.WhenAll(responses.Where(r => r.StatusCode == HttpStatusCode.Created).Select(r => r.Content.ReadFromJsonAsync<SaleRow>(JsonOptions)));
        await Task.WhenAll(bills.Select(b => api.PayAndDeliverAsync(pharmacist, b!.Id)));

        var stored = Assert.Single((await pharmacist.GetFromJsonAsync<List<PrescriptionDto>>("/api/prescriptions?q=" + rx.RxNumber, JsonOptions))!);
        Assert.Equal(sold, stored.Items[0].DispensedQuantity);

        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(100 - 1 - sold, await db.StockBatches.AsNoTracking().Where(b => b.ProductId == productId).SumAsync(b => b.Quantity));
    }

    [Fact]
    public async Task Sale_that_fails_halfway_leaves_nothing_behind_and_can_be_retried()
    {
        var plenty = await NewProductAsync(10);
        var scarce = await NewProductAsync(1);
        var reception = await UserAsync(Roles.Receptionist);
        var patient = await Created<PatientDto>(await reception.SendAsync(Post("/api/patients", NewPatient())));
        var pharmacist = await UserAsync(Roles.Pharmacist);
        var key = Guid.NewGuid().ToString("N");

        // Lines are taken in product order: the first is already saved inside the transaction when the second fails.
        var request = new CreateSaleRequest(patient.Id, null, [new SaleLineInput(Math.Min(plenty, scarce), 1), new SaleLineInput(Math.Max(plenty, scarce), 5)]);
        if (plenty > scarce)
        {
            request = new CreateSaleRequest(patient.Id, null, [new SaleLineInput(scarce, 1), new SaleLineInput(plenty, 11)]);
        }

        var failed = await pharmacist.SendAsync(Post("/api/pharmacy/sales", request, key));
        Assert.Equal(HttpStatusCode.Conflict, failed.StatusCode);
        Assert.Contains("insufficient_stock", await failed.Content.ReadAsStringAsync());

        using (var scope = api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(10, await db.StockBatches.AsNoTracking().Where(b => b.ProductId == plenty).SumAsync(b => b.Quantity));
            Assert.Equal(1, await db.StockBatches.AsNoTracking().Where(b => b.ProductId == scarce).SumAsync(b => b.Quantity));
            Assert.False(await db.Sales.AsNoTracking().AnyAsync(s => s.PatientId == patient.Id));
            Assert.False(await db.Invoices.AsNoTracking().AnyAsync(i => i.PatientId == patient.Id));
            Assert.False(await db.IdempotencyRecords.AsNoTracking().AnyAsync(r => r.Key == key));
        }

        await AssertLedgerReconciles();

        // Nothing was kept, so the same key runs the corrected action rather than replaying the failure.
        var retry = await pharmacist.SendAsync(Post("/api/pharmacy/sales", new CreateSaleRequest(patient.Id, null, [new SaleLineInput(plenty, 1)]), key));
        Assert.True(retry.StatusCode == HttpStatusCode.Created, await retry.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(Roles.Receptionist, "/api/invoices", false)]
    [InlineData(Roles.Pharmacist, "/api/users", false)]
    [InlineData(Roles.Cashier, "/api/prescriptions", false)]
    [InlineData(Roles.Doctor, "/api/ledger/balances", false)]
    [InlineData(Roles.Manager, "/api/ledger/balances", true)]
    public async Task Department_permissions_are_enforced_on_direct_api_reads(string role, string url, bool allowed)
    {
        var client = await UserAsync(role);
        var response = await client.GetAsync(url);
        Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Director_role_can_read_but_not_write()
    {
        var manager = await UserAsync(Roles.Manager);
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync("/api/patients")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.SendAsync(Post("/api/patients", NewPatient()))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.SendAsync(Post("/api/refunds", new RefundRequest(1, 1, "تلاش غیرمجاز")))).StatusCode);
    }

    [Fact]
    public void Every_api_endpoint_requires_a_signed_in_user_except_login_and_csrf()
    {
        var open = api.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.StartsWith("/api") || e.RoutePattern.RawText!.StartsWith("/hubs"))
            .Where(e => e.Metadata.GetMetadata<IAuthorizeData>() is null)
            .Select(e => e.RoutePattern.RawText!)
            .Distinct()
            .Order()
            .ToList();

        Assert.Equal(["/api/auth/csrf", "/api/auth/login"], open);
    }

    [Fact]
    public async Task Departments_working_together_keep_records_stock_and_money_consistent()
    {
        var serviceId = await NewServiceAsync(500);
        var productId = await NewProductAsync(100);
        var drug = await api.InDbAsync(db => db.Products.Where(p => p.Id == productId).Select(p => p.Name).SingleAsync());
        var reception = await UserAsync(Roles.Receptionist);
        var doctor = await UserAsync(Roles.Doctor);
        var pharmacist = await UserAsync(Roles.Pharmacist);
        var cashier = await CashierWithOpenSessionAsync();
        var department = await DepartmentAsync(reception, "داکتر");

        async Task<(long VisitId, int Queue)> OnePatientAsync()
        {
            var patient = await Created<PatientDto>(await reception.SendAsync(Post("/api/patients", NewPatient())));
            var visit = await Created<VisitRow>(await reception.SendAsync(Post("/api/visits", new CreateVisitRequest(patient.Id, department, null, "معاینه", serviceId, await api.PaidTokenAsync()))));

            async Task PayOpenAsync()
            {
                var unpaid = await cashier.GetFromJsonAsync<PagedResult<InvoiceRow>>($"/api/invoices?unpaid=true&q={patient.FileNumber}", JsonOptions);
                foreach (var invoice in unpaid!.Items)
                {
                    await Created<PaymentRow>(await cashier.SendAsync(Post("/api/payments", new PaymentRequest(invoice.Id, invoice.Balance, PaymentMethod.Cash, null, invoice.Version))));
                }
            }

            await api.CompleteVisionAsync(visit.Id);

            // The extra service chosen at registration is paid before the doctor starts.
            await PayOpenAsync();
            var saved = await doctor.PutAsJsonAsync($"/api/consultations/{visit.Id}", new SaveConsultationRequest("یافته OD", "یافته OS", "درمان", null, [new DiagnosisInput(EyeSide.OD, "تشخیص")], null));
            Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
            var rx = await Created<PrescriptionDto>(await doctor.SendAsync(Post("/api/prescriptions", new CreatePrescriptionRequest(visit.Id, null, [new RxItemInput(drug, null, null, null, null, 2)]))));

            var sale = await Created<SaleRow>(await pharmacist.SendAsync(Post("/api/pharmacy/sales", new CreateSaleRequest(patient.Id, rx.Id, [new SaleLineInput(productId, 2, rx.Items[0].Id)]))));

            await PayOpenAsync();
            await api.PayAndDeliverAsync(pharmacist, sale.Id);

            var settled = await cashier.GetFromJsonAsync<PagedResult<InvoiceRow>>($"/api/invoices?unpaid=true&q={patient.FileNumber}", JsonOptions);
            Assert.Empty(settled!.Items);
            Assert.Equal(200, sale.Total);
            return (visit.Id, visit.QueueNumber!.Value);
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => OnePatientAsync()));

        Assert.Equal(12, results.Select(r => r.Queue).Distinct().Count());

        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(100 - (12 * 2), await db.StockBatches.AsNoTracking().Where(b => b.ProductId == productId).SumAsync(b => b.Quantity));
        await AssertLedgerReconciles();
    }

    [Fact]
    public async Task Backup_status_is_visible_to_management_only()
    {
        var manager = await UserAsync(Roles.Manager);
        var status = await manager.GetFromJsonAsync<BackupStatusDto>("/api/settings/backup-status", JsonOptions);
        Assert.False(status!.Configured);

        var doctor = await UserAsync(Roles.Doctor);
        Assert.Equal(HttpStatusCode.Forbidden, (await doctor.GetAsync("/api/settings/backup-status")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await doctor.PostAsync("/api/settings/backup/run", null)).StatusCode);
    }

    [Fact]
    public async Task Backup_can_be_started_from_the_application_once_at_a_time()
    {
        var admin = await UserAsync(Roles.SuperAdmin);
        var before = api.BackupTrigger.Requests;

        // Development and test machines have no scheduled backup to start.
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync("/api/settings/backup/run", null)).StatusCode);

        var config = api.Services.GetRequiredService<IConfiguration>();
        var path = Path.Combine(Path.GetTempPath(), $"alshifa-backup-status-{Guid.NewGuid():N}.json");
        config["Backup:StatusFile"] = path;
        try
        {
            File.WriteAllText(path, $$"""{"lastSuccessAt":"{{DateTime.UtcNow.AddHours(-1):o}}","runningSince":"{{DateTime.UtcNow.AddMinutes(-1):o}}"}""");
            Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync("/api/settings/backup/run", null)).StatusCode);

            File.WriteAllText(path, $$"""{"lastSuccessAt":"{{DateTime.UtcNow.AddHours(-1):o}}","backups":[{"name":"alshifa-20261108T060000Z.zip.gpg","sizeBytes":1024,"createdAt":"2026-11-08T06:00:00Z"}]}""");
            var status = await admin.GetFromJsonAsync<BackupStatusDto>("/api/settings/backup-status", JsonOptions);
            Assert.Single(status!.Backups);

            Assert.Equal(HttpStatusCode.Accepted, (await admin.PostAsync("/api/settings/backup/run", null)).StatusCode);
            // A second click before the first backup could finish does not start another one.
            Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync("/api/settings/backup/run", null)).StatusCode);
            Assert.Equal(before + 1, api.BackupTrigger.Requests);
        }
        finally
        {
            config["Backup:StatusFile"] = null;
            File.Delete(path);
        }
    }
}
