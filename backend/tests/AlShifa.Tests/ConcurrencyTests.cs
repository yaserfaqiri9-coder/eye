using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Clinical;
using AlShifa.Api.Features.Finance;
using AlShifa.Api.Features.Inventory;
using AlShifa.Api.Features.Patients;
using AlShifa.Api.Features.Users;
using AlShifa.Api.Features.Visits;
using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AlShifa.Tests;

/// <summary>Several users acting on one record at the same moment: at most one succeeds and nothing is lost silently.</summary>
[Collection("api")]
public sealed class ConcurrencyTests(ApiFixture api)
{
    private const int Racers = 6;

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

    private async Task<InvoiceRow> NewInvoiceAsync(decimal price)
    {
        var serviceId = await NewServiceAsync(price);
        var cashier = await UserAsync(Roles.Cashier);
        return await Created<InvoiceRow>(await cashier.SendAsync(Post("/api/invoices", new CreateInvoiceRequest(null, null, [new InvoiceLineInput(serviceId, 1)]))));
    }

    private async Task AssertLedgerReconciles()
    {
        using var scope = api.Services.CreateScope();
        var result = await FinanceEndpoints.ReconcileAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), default);
        Assert.True(result.Balanced, JsonSerializer.Serialize(result));
    }

    /// <summary>Starts every attempt together, and checks one won and the rest were told so rather than failing or silently overwriting.</summary>
    private static async Task<int> AssertOneWinnerAsync(Func<int, Task<HttpResponseMessage>> attempt, HttpStatusCode success)
    {
        var responses = await Task.WhenAll(Enumerable.Range(0, Racers).Select(attempt));
        var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()));
        var report = string.Join("\n", responses.Zip(bodies, (r, b) => $"{(int)r.StatusCode} {b}"));

        var winners = Enumerable.Range(0, Racers).Where(i => responses[i].StatusCode == success).ToList();
        Assert.True(winners.Count == 1, report);
        Assert.True(responses.All(r => r.StatusCode == success || r.StatusCode == HttpStatusCode.Conflict), report);
        return winners[0];
    }

    [Fact]
    public async Task Simultaneous_patient_edits_have_one_winner_and_its_values_are_what_is_stored()
    {
        var reception = await UserAsync(Roles.Receptionist);
        var patient = await Created<PatientDto>(await reception.SendAsync(Post("/api/patients", new SavePatientRequest(Name("احمد"), null, null, Gender.Male, null, 30, null, null))));

        var winner = await AssertOneWinnerAsync(
            i => reception.PutAsJsonAsync($"/api/patients/{patient.Id}", new SavePatientRequest(patient.FirstName, null, null, Gender.Male, null, 30, null, $"آدرس-{i}", Version: patient.Version)),
            HttpStatusCode.OK);

        var stored = await reception.GetFromJsonAsync<PatientDto>($"/api/patients/{patient.Id}");
        Assert.Equal($"آدرس-{winner}", stored!.Address);
        Assert.Equal(patient.Version + 1, stored.Version);

        // The change history holds exactly the change that happened.
        var changes = await reception.GetFromJsonAsync<List<PatientChangeDto>>($"/api/patients/{patient.Id}/changes");
        Assert.Equal($"آدرس-{winner}", Assert.Single(changes!, c => c.Field == "Address").NewValue);
    }

    [Fact]
    public async Task Simultaneous_pre_exam_edits_have_one_winner()
    {
        var reception = await UserAsync(Roles.Receptionist);
        var patient = await Created<PatientDto>(await reception.SendAsync(Post("/api/patients", new SavePatientRequest(Name("احمد"), null, null, Gender.Male, null, 30, null, null))));
        var departments = await reception.GetFromJsonAsync<List<LookupItem>>("/api/lookups/departments");
        var visit = await Created<VisitRow>(await reception.SendAsync(Post("/api/visits", new CreateVisitRequest(patient.Id, (int)departments!.Single(d => d.Name == "معاینه ابتدایی").Id, null, null))));

        var triage = await UserAsync(Roles.Triage);
        var created = await triage.PutAsJsonAsync($"/api/pre-examinations/{visit.Id}", new SavePreExamRequest("شکایت", null, "6/9", "6/9", "15", "15", null, null, null));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var version = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("version").GetInt32();

        var winner = await AssertOneWinnerAsync(
            i => triage.PutAsJsonAsync($"/api/pre-examinations/{visit.Id}", new SavePreExamRequest($"شکایت-{i}", null, "6/9", "6/9", "15", "15", null, null, version)),
            HttpStatusCode.OK);

        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal($"شکایت-{winner}", (await db.PreExaminations.AsNoTracking().SingleAsync(p => p.VisitId == visit.Id)).ChiefComplaint);
    }

    [Fact]
    public async Task A_retry_after_a_lost_answer_gets_the_same_receipt_and_changes_nothing()
    {
        var invoice = await NewInvoiceAsync(1000);
        var cashier = await CashierWithOpenSessionAsync();
        var key = Guid.NewGuid().ToString("N");
        var request = new PaymentRequest(invoice.Id, 400, PaymentMethod.Cash, null);

        var first = await Created<PaymentRow>(await cashier.SendAsync(Post("/api/payments", request, key)));

        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ledgerBefore = await db.LedgerTransactions.CountAsync();

        var retried = await Created<PaymentRow>(await cashier.SendAsync(Post("/api/payments", request, key)));

        Assert.Equal(first.ReceiptNumber, retried.ReceiptNumber);
        Assert.Equal(ledgerBefore, await db.LedgerTransactions.CountAsync());
        var detail = await cashier.GetFromJsonAsync<InvoiceDetail>($"/api/invoices/{invoice.Id}", JsonOptions);
        Assert.Single(detail!.Payments);
        Assert.Equal(600, detail.Invoice.Balance);
    }

    [Fact]
    public async Task One_key_cannot_stand_for_a_different_payment()
    {
        var invoice = await NewInvoiceAsync(1000);
        var cashier = await CashierWithOpenSessionAsync();
        var key = Guid.NewGuid().ToString("N");

        await Created<PaymentRow>(await cashier.SendAsync(Post("/api/payments", new PaymentRequest(invoice.Id, 100, PaymentMethod.Cash, null), key)));
        var other = await cashier.SendAsync(Post("/api/payments", new PaymentRequest(invoice.Id, 200, PaymentMethod.Cash, null), key));

        Assert.Equal(HttpStatusCode.Conflict, other.StatusCode);
        Assert.Contains("idempotency_key_reused", await other.Content.ReadAsStringAsync());
        Assert.Equal(900, (await cashier.GetFromJsonAsync<InvoiceDetail>($"/api/invoices/{invoice.Id}", JsonOptions))!.Invoice.Balance);
    }

    [Fact]
    public async Task Many_cashiers_paying_the_full_balance_together_produce_one_payment()
    {
        var invoice = await NewInvoiceAsync(700);
        var cashiers = await Task.WhenAll(Enumerable.Range(0, Racers).Select(_ => CashierWithOpenSessionAsync()));

        await AssertOneWinnerAsync(i => cashiers[i].SendAsync(Post("/api/payments", new PaymentRequest(invoice.Id, 700, PaymentMethod.Cash, null))), HttpStatusCode.Created);

        var detail = await cashiers[0].GetFromJsonAsync<InvoiceDetail>($"/api/invoices/{invoice.Id}", JsonOptions);
        Assert.Single(detail!.Payments);
        Assert.Equal(0, detail.Invoice.Balance);
        await AssertLedgerReconciles();
    }

    [Fact]
    public async Task Refunding_one_payment_many_times_together_returns_the_money_once()
    {
        var invoice = await NewInvoiceAsync(500);
        var cashier = await CashierWithOpenSessionAsync();
        var payment = await Created<PaymentRow>(await cashier.SendAsync(Post("/api/payments", new PaymentRequest(invoice.Id, 500, PaymentMethod.Cash, null))));
        var accountant = await UserAsync(Roles.Accountant);

        await AssertOneWinnerAsync(_ => accountant.SendAsync(Post("/api/refunds", new RefundRequest(payment.Id, 500, "برگشت آزمایشی"))), HttpStatusCode.Created);

        Assert.Equal(500, (await cashier.GetFromJsonAsync<InvoiceDetail>($"/api/invoices/{invoice.Id}", JsonOptions))!.Payments.Single().Refunded);
        await AssertLedgerReconciles();
    }

    [Fact]
    public async Task Voiding_while_paying_never_leaves_money_on_a_void_invoice()
    {
        foreach (var _ in Enumerable.Range(0, 5))
        {
            var invoice = await NewInvoiceAsync(300);
            var cashier = await CashierWithOpenSessionAsync();

            var pay = cashier.SendAsync(Post("/api/payments", new PaymentRequest(invoice.Id, 300, PaymentMethod.Cash, null)));
            var cancel = cashier.PostAsJsonAsync($"/api/invoices/{invoice.Id}/void", new VoidRequest("لغو آزمایشی", invoice.Version));
            await Task.WhenAll(pay, cancel);

            var detail = await cashier.GetFromJsonAsync<InvoiceDetail>($"/api/invoices/{invoice.Id}", JsonOptions);
            if (detail!.Invoice.Status == nameof(InvoiceStatus.Void))
            {
                Assert.Empty(detail.Payments);
            }
            else
            {
                Assert.Single(detail.Payments);
                Assert.Equal(nameof(InvoiceStatus.Paid), detail.Invoice.Status);
            }
        }

        await AssertLedgerReconciles();
    }

    private async Task<(long PatientId, long RxId, long ItemId, int ProductId)> PrescriptionInStockAsync(decimal prescribed)
    {
        var manager = await UserAsync(Roles.Inventory);
        var productResponse = await manager.PostAsJsonAsync("/api/inventory/products", new SaveProductRequest(Name("D"), "جنس " + Name("n"), ProductKind.Drug, null, "عدد", 0, 100));
        var productId = JsonDocument.Parse(await productResponse.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.Created, (await manager.SendAsync(Post("/api/inventory/purchases", new CreatePurchaseRequest("تأمین‌کننده", "pharmacy",
            [new PurchaseLineInput(productId, Name("L"), DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)), 100, 40)])))).StatusCode);

        var reception = await UserAsync(Roles.Receptionist);
        var patient = await Created<PatientDto>(await reception.SendAsync(Post("/api/patients", new SavePatientRequest(Name("احمد"), null, null, Gender.Male, null, 30, null, null))));
        var departments = await reception.GetFromJsonAsync<List<LookupItem>>("/api/lookups/departments");
        var visit = await Created<VisitRow>(await reception.SendAsync(Post("/api/visits", new CreateVisitRequest(patient.Id, (int)departments!.Single(d => d.Name == "داکتر").Id, null, null))));
        var doctor = await UserAsync(Roles.Doctor);
        var rx = await Created<PrescriptionDto>(await doctor.SendAsync(Post("/api/prescriptions", new CreatePrescriptionRequest(visit.Id, null, [new RxItemInput("دوا", null, null, null, null, prescribed)]))));

        using var scope = api.Services.CreateScope();
        var itemId = (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Set<MedicalPrescriptionItem>().AsNoTracking().SingleAsync(i => i.PrescriptionId == rx.Id)).Id;
        return (patient.Id, rx.Id, itemId, productId);
    }

    [Fact]
    public async Task Two_pharmacists_cannot_dispense_more_than_was_prescribed()
    {
        var rx = await PrescriptionInStockAsync(2);
        var pharmacists = await Task.WhenAll(Enumerable.Range(0, Racers).Select(_ => UserAsync(Roles.Pharmacist)));
        var responses = await Task.WhenAll(pharmacists.Select(p => p.SendAsync(Post("/api/pharmacy/sales", new CreateSaleRequest(rx.PatientId, rx.RxId, [new SaleLineInput(rx.ProductId, 2, rx.ItemId)])))));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        using var check = api.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(2, (await db.Set<MedicalPrescriptionItem>().AsNoTracking().SingleAsync(i => i.Id == rx.ItemId)).DispensedQuantity);
        Assert.Equal(98, await db.StockBatches.Where(b => b.ProductId == rx.ProductId).SumAsync(b => b.Quantity));
    }

    [Fact]
    public async Task Partial_dispensing_at_the_same_moment_counts_every_unit_that_left_the_shelf()
    {
        var rx = await PrescriptionInStockAsync(50);
        var pharmacists = await Task.WhenAll(Enumerable.Range(0, Racers).Select(_ => UserAsync(Roles.Pharmacist)));

        // Already partly dispensed, so the prescription's status does not change and cannot reveal the overlap.
        Assert.Equal(HttpStatusCode.Created, (await pharmacists[0].SendAsync(Post("/api/pharmacy/sales", new CreateSaleRequest(rx.PatientId, rx.RxId, [new SaleLineInput(rx.ProductId, 1, rx.ItemId)])))).StatusCode);

        var responses = await Task.WhenAll(pharmacists.Select(p => p.SendAsync(Post("/api/pharmacy/sales", new CreateSaleRequest(rx.PatientId, rx.RxId, [new SaleLineInput(rx.ProductId, 2, rx.ItemId)])))));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        using var check = api.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1 + (2 * Racers), (await db.Set<MedicalPrescriptionItem>().AsNoTracking().SingleAsync(i => i.Id == rx.ItemId)).DispensedQuantity);
        Assert.Equal(100 - 1 - (2 * Racers), await db.StockBatches.Where(b => b.ProductId == rx.ProductId).SumAsync(b => b.Quantity));
    }

    [Fact]
    public async Task Closing_a_session_while_payments_arrive_never_leaves_a_payment_outside_the_counted_total()
    {
        foreach (var _ in Enumerable.Range(0, 5))
        {
            var cashier = await CashierWithOpenSessionAsync();
            var invoices = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => NewInvoiceAsync(100)));
            var sessions = await cashier.GetFromJsonAsync<List<SessionRow>>("/api/cash/sessions?mine=true", JsonOptions);
            var session = Assert.Single(sessions!);

            var pays = invoices.Select(i => cashier.SendAsync(Post("/api/payments", new PaymentRequest(i.Id, 100, PaymentMethod.Cash, null)))).ToList();
            var close = cashier.PostAsJsonAsync($"/api/cash/sessions/{session.Id}/close", new CloseSessionRequest(0, session.Version));
            await Task.WhenAll(pays.Append(close));
            Assert.Equal(HttpStatusCode.OK, close.Result.StatusCode);

            var closed = (await cashier.GetFromJsonAsync<List<SessionRow>>("/api/cash/sessions?mine=true", JsonOptions))!.Single();
            Assert.Equal(closed.CashCollected, closed.ExpectedAmount);
        }
    }

    [Fact]
    public async Task A_refunded_sale_is_refused_a_return_with_a_clear_answer_and_stock_stays_put()
    {
        var rx = await PrescriptionInStockAsync(5);
        var pharmacist = await UserAsync(Roles.Pharmacist);
        var sale = await Created<SaleRow>(await pharmacist.SendAsync(Post("/api/pharmacy/sales", new CreateSaleRequest(rx.PatientId, rx.RxId, [new SaleLineInput(rx.ProductId, 1, rx.ItemId)]))));

        var cashier = await CashierWithOpenSessionAsync();
        var payment = await Created<PaymentRow>(await cashier.SendAsync(Post("/api/payments", new PaymentRequest(sale.InvoiceId, sale.Balance, PaymentMethod.Cash, null))));
        var accountant = await UserAsync(Roles.Accountant);
        Assert.Equal(HttpStatusCode.Created, (await accountant.SendAsync(Post("/api/refunds", new RefundRequest(payment.Id, payment.Amount, "برگشت آزمایشی")))).StatusCode);

        var returned = await pharmacist.PostAsJsonAsync($"/api/pharmacy/sales/{sale.Id}/return", new ReturnSaleRequest(sale.Version, "برگشت جنس"));

        Assert.Equal(HttpStatusCode.Conflict, returned.StatusCode);
        using var scope = api.Services.CreateScope();
        Assert.Equal(99, await scope.ServiceProvider.GetRequiredService<AppDbContext>().StockBatches.Where(b => b.ProductId == rx.ProductId).SumAsync(b => b.Quantity));
        await AssertLedgerReconciles();
    }

    /// <summary>
    /// Pins today's unapproved cash rules so a change cannot happen by accident. See FINANCE-DECISIONS.md.
    /// When the accountant decides, this test changes along with the rule.
    /// </summary>
    [Fact]
    public async Task Current_rule_expected_cash_ignores_refunds_expenses_and_other_methods_while_the_ledger_counts_them_all()
    {
        var accountant = await UserAsync(Roles.Accountant);
        var cashier = await UserAsync(Roles.Cashier);

        // Earlier tests may have left ledger movement, so only the change caused here is measured.
        async Task<decimal> LedgerCashAsync()
        {
            using var scope = api.Services.CreateScope();
            return (await FinanceEndpoints.ReconcileAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), default)).LedgerCash;
        }

        int registerId;
        using (var scope = api.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var register = new CashRegister { Name = Name("صندوق") };
            db.CashRegisters.Add(register);
            await db.SaveChangesAsync();
            registerId = register.Id;
        }

        Assert.Equal(HttpStatusCode.Created, (await cashier.PostAsJsonAsync("/api/cash/sessions", new OpenSessionRequest(registerId, 1000))).StatusCode);
        var ledgerBefore = await LedgerCashAsync();

        var cashInvoice = await NewInvoiceAsync(500);
        var otherInvoice = await NewInvoiceAsync(200);
        var cashPayment = await Created<PaymentRow>(await cashier.SendAsync(Post("/api/payments", new PaymentRequest(cashInvoice.Id, 500, PaymentMethod.Cash, null))));
        await Created<PaymentRow>(await cashier.SendAsync(Post("/api/payments", new PaymentRequest(otherInvoice.Id, 200, PaymentMethod.Other, "حواله"))));
        Assert.Equal(HttpStatusCode.Created, (await accountant.SendAsync(Post("/api/refunds", new RefundRequest(cashPayment.Id, 100, "برگشت آزمایشی")))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await accountant.SendAsync(Post("/api/expenses", new CreateExpenseRequest("قرطاسیه", null, 50, DateOnly.FromDateTime(DateTime.UtcNow))))).StatusCode);

        var session = (await cashier.GetFromJsonAsync<List<SessionRow>>("/api/cash/sessions?mine=true", JsonOptions))!.Single();
        var closed = await cashier.PostAsJsonAsync($"/api/cash/sessions/{session.Id}/close", new CloseSessionRequest(1350, session.Version));
        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
        var result = JsonSerializer.Deserialize<SessionRow>(await closed.Content.ReadAsStringAsync(), JsonOptions)!;

        // Session: opening 1000 + cash 500. The refund, the expense and the non-cash 200 play no part.
        Assert.Equal(500, result.CashCollected);
        Assert.Equal(1500, result.ExpectedAmount);

        // Ledger cash account: 500 + 200 - 100 - 50. The opening float is not in the ledger at all.
        Assert.Equal(550, await LedgerCashAsync() - ledgerBefore);

        // A drawer that really paid out the refund and the expense holds 1350, and shows as 150 short. No ledger entry records that difference.
        Assert.Equal(1350, result.CountedAmount);
        await AssertLedgerReconciles();
    }
}
