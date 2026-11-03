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

[Collection("api")]
public sealed class FinanceTests(ApiFixture api)
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

    private async Task<PatientDto> NewPatientAsync()
    {
        var reception = await UserAsync(Roles.Receptionist);
        return await Created<PatientDto>(await reception.SendAsync(Post("/api/patients", new SavePatientRequest(Name("مریض"), null, null, Gender.Male, null, 30, null, null))));
    }

    private async Task<HttpClient> CashierWithOpenSessionAsync()
    {
        var cashier = await UserAsync(Roles.Cashier);
        await OpenFreshSessionAsync(cashier);
        return cashier;
    }

    /// <summary>Each test cashier gets a register of their own, since a register has one open session at a time.</summary>
    private async Task OpenFreshSessionAsync(HttpClient cashier)
    {
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
    }

    private async Task<InvoiceRow> NewInvoiceAsync(long patientId, int serviceId)
    {
        var cashier = await UserAsync(Roles.Cashier);
        return await Created<InvoiceRow>(await cashier.SendAsync(Post("/api/invoices", new CreateInvoiceRequest(patientId, null, [new InvoiceLineInput(serviceId, 1)]))));
    }

    private async Task<int> NewProductAsync(decimal salePrice = 100, ProductKind kind = ProductKind.Drug)
    {
        var manager = await UserAsync(Roles.Inventory);
        var response = await manager.PostAsJsonAsync("/api/inventory/products", new SaveProductRequest(Name("D"), "جنس " + Name("n"), kind, null, "عدد", 0, salePrice));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt32();
    }

    private async Task StockAsync(int productId, decimal quantity, string location = "pharmacy", decimal cost = 40)
    {
        var manager = await UserAsync(Roles.Inventory);
        var response = await manager.SendAsync(Post("/api/inventory/purchases", new CreatePurchaseRequest("تأمین‌کننده", location,
            [new PurchaseLineInput(productId, Name("L"), DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)), quantity, cost)])));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }

    private async Task AssertLedgerReconciles()
    {
        using var scope = api.Services.CreateScope();
        var result = await FinanceEndpoints.ReconcileAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), default);
        Assert.True(result.Balanced, JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task Visit_service_charge_appears_at_the_cashier_with_a_price_snapshot()
    {
        var serviceId = await NewServiceAsync(500);
        var patient = await NewPatientAsync();
        var reception = await UserAsync(Roles.Receptionist);
        var departments = await reception.GetFromJsonAsync<List<LookupItem>>("/api/lookups/departments");
        var visit = await Created<VisitRow>(await reception.SendAsync(Post("/api/visits", new CreateVisitRequest(patient.Id, (int)departments![0].Id, null, null, serviceId, await api.PaidTokenAsync()))));

        var cashier = await UserAsync(Roles.Cashier);
        var unpaid = await cashier.GetFromJsonAsync<PagedResult<InvoiceRow>>($"/api/invoices?unpaid=true&q={patient.FileNumber}", JsonOptions);
        var invoice = Assert.Single(unpaid!.Items);
        Assert.Equal(visit.Id, invoice.VisitId);
        Assert.Equal(500, invoice.Balance);

        var admin = await UserAsync(Roles.SuperAdmin);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/services/{serviceId}/price", new SetPriceRequest(900))).StatusCode);

        var detail = await cashier.GetFromJsonAsync<InvoiceDetail>($"/api/invoices/{invoice.Id}", JsonOptions);
        Assert.Equal(500, Assert.Single(detail!.Items).UnitPrice);
        await AssertLedgerReconciles();
    }

    [Fact]
    public async Task Same_source_cannot_be_charged_twice()
    {
        var serviceId = await NewServiceAsync(100);
        var patient = await NewPatientAsync();
        var reception = await UserAsync(Roles.Receptionist);
        var departments = await reception.GetFromJsonAsync<List<LookupItem>>("/api/lookups/departments");
        var visit = await Created<VisitRow>(await reception.SendAsync(Post("/api/visits", new CreateVisitRequest(patient.Id, (int)departments![0].Id, null, null, serviceId, await api.PaidTokenAsync()))));

        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var item = await db.InvoiceItems.AsNoTracking().SingleAsync(i => i.SourceType == nameof(Visit) && i.SourceId == visit.Id);

        db.InvoiceItems.Add(new InvoiceItem { InvoiceId = item.InvoiceId, Description = "تکراری", UnitPrice = 100, LineTotal = 100, RevenueAccount = Accounts.ServiceRevenue, SourceType = nameof(Visit), SourceId = visit.Id });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Partial_payment_leaves_the_correct_balance_and_overpayment_is_refused()
    {
        var serviceId = await NewServiceAsync(1000);
        var patient = await NewPatientAsync();
        var invoice = await NewInvoiceAsync(patient.Id, serviceId);
        var cashier = await CashierWithOpenSessionAsync();

        await Created<PaymentRow>(await cashier.SendAsync(Post("/api/payments", new PaymentRequest(invoice.Id, 400, PaymentMethod.Cash, null))));

        var row = await cashier.GetFromJsonAsync<InvoiceDetail>($"/api/invoices/{invoice.Id}", JsonOptions);
        Assert.Equal(600, row!.Invoice.Balance);
        Assert.Equal(nameof(InvoiceStatus.PartiallyPaid), row.Invoice.Status);

        var over = await cashier.SendAsync(Post("/api/payments", new PaymentRequest(invoice.Id, 601, PaymentMethod.Cash, null)));
        Assert.Equal(HttpStatusCode.Conflict, over.StatusCode);
        Assert.Contains("overpayment", await over.Content.ReadAsStringAsync());

        await Created<PaymentRow>(await cashier.SendAsync(Post("/api/payments", new PaymentRequest(invoice.Id, 600, PaymentMethod.Cash, null))));
        row = await cashier.GetFromJsonAsync<InvoiceDetail>($"/api/invoices/{invoice.Id}", JsonOptions);
        Assert.Equal(0, row!.Invoice.Balance);
        Assert.Equal(nameof(InvoiceStatus.Paid), row.Invoice.Status);
        await AssertLedgerReconciles();
    }

    [Fact]
    public async Task Same_payment_sent_concurrently_is_recorded_once()
    {
        var serviceId = await NewServiceAsync(1000);
        var patient = await NewPatientAsync();
        var invoice = await NewInvoiceAsync(patient.Id, serviceId);
        var cashier = await CashierWithOpenSessionAsync();
        var key = Guid.NewGuid().ToString("N");

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            cashier.SendAsync(Post("/api/payments", new PaymentRequest(invoice.Id, 300, PaymentMethod.Cash, null), key))));
        var receipts = await Task.WhenAll(responses.Select(r => r.Content.ReadFromJsonAsync<PaymentRow>(JsonOptions)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        Assert.Single(receipts.Select(r => r!.ReceiptNumber).Distinct());

        var row = await cashier.GetFromJsonAsync<InvoiceDetail>($"/api/invoices/{invoice.Id}", JsonOptions);
        Assert.Single(row!.Payments);
        Assert.Equal(700, row.Invoice.Balance);
    }

    [Fact]
    public async Task Two_cashiers_cannot_overpay_one_invoice_together()
    {
        var serviceId = await NewServiceAsync(1000);
        var patient = await NewPatientAsync();
        var invoice = await NewInvoiceAsync(patient.Id, serviceId);
        var first = await CashierWithOpenSessionAsync();
        var second = await CashierWithOpenSessionAsync();

        var responses = await Task.WhenAll(
            first.SendAsync(Post("/api/payments", new PaymentRequest(invoice.Id, 800, PaymentMethod.Cash, null))),
            second.SendAsync(Post("/api/payments", new PaymentRequest(invoice.Id, 800, PaymentMethod.Cash, null))));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        var row = await first.GetFromJsonAsync<InvoiceDetail>($"/api/invoices/{invoice.Id}", JsonOptions);
        Assert.Equal(200, row!.Invoice.Balance);
        await AssertLedgerReconciles();
    }

    [Fact]
    public async Task Payment_needs_an_open_cash_session_and_a_cashier_cannot_refund()
    {
        var serviceId = await NewServiceAsync(100);
        var patient = await NewPatientAsync();
        var invoice = await NewInvoiceAsync(patient.Id, serviceId);

        var cashier = await UserAsync(Roles.Cashier);
        var closed = await cashier.SendAsync(Post("/api/payments", new PaymentRequest(invoice.Id, 100, PaymentMethod.Cash, null)));
        Assert.Contains("no_open_session", await closed.Content.ReadAsStringAsync());

        await OpenFreshSessionAsync(cashier);
        var payment = await Created<PaymentRow>(await cashier.SendAsync(Post("/api/payments", new PaymentRequest(invoice.Id, 100, PaymentMethod.Cash, null))));

        var denied = await cashier.SendAsync(Post("/api/refunds", new RefundRequest(payment.Id, 50, "تلاش غیرمجاز")));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task Refund_is_limited_to_the_payment_and_the_ledger_stays_balanced()
    {
        var serviceId = await NewServiceAsync(1000);
        var patient = await NewPatientAsync();
        var invoice = await NewInvoiceAsync(patient.Id, serviceId);
        var cashier = await CashierWithOpenSessionAsync();
        var payment = await Created<PaymentRow>(await cashier.SendAsync(Post("/api/payments", new PaymentRequest(invoice.Id, 1000, PaymentMethod.Cash, null))));

        var accountant = await UserAsync(Roles.Accountant);
        await Created<object>(await accountant.SendAsync(Post("/api/refunds", new RefundRequest(payment.Id, 300, "برگشت جزئی"))));

        var over = await accountant.SendAsync(Post("/api/refunds", new RefundRequest(payment.Id, 701, "بیش از حد")));
        Assert.Contains("refund_too_large", await over.Content.ReadAsStringAsync());

        var row = await accountant.GetFromJsonAsync<InvoiceDetail>($"/api/invoices/{invoice.Id}", JsonOptions);
        Assert.Equal(300, row!.Invoice.Refunded);
        await AssertLedgerReconciles();
    }

    [Fact]
    public async Task Invoice_with_a_payment_cannot_be_voided_and_unpaid_void_reverses_the_ledger()
    {
        var serviceId = await NewServiceAsync(100);
        var patient = await NewPatientAsync();
        var paid = await NewInvoiceAsync(patient.Id, serviceId);
        var cashier = await CashierWithOpenSessionAsync();
        await Created<PaymentRow>(await cashier.SendAsync(Post("/api/payments", new PaymentRequest(paid.Id, 50, PaymentMethod.Cash, null))));

        var current = await cashier.GetFromJsonAsync<InvoiceDetail>($"/api/invoices/{paid.Id}", JsonOptions);
        var denied = await cashier.PostAsJsonAsync($"/api/invoices/{paid.Id}/void", new VoidRequest("اشتباه", current!.Invoice.Version));
        Assert.Contains("invoice_has_payments", await denied.Content.ReadAsStringAsync());

        var unpaid = await NewInvoiceAsync(patient.Id, serviceId);
        var fresh = await cashier.GetFromJsonAsync<InvoiceDetail>($"/api/invoices/{unpaid.Id}", JsonOptions);
        Assert.Equal(HttpStatusCode.NoContent, (await cashier.PostAsJsonAsync($"/api/invoices/{unpaid.Id}/void", new VoidRequest("اشتباه", fresh!.Invoice.Version))).StatusCode);

        var after = await cashier.GetFromJsonAsync<InvoiceDetail>($"/api/invoices/{unpaid.Id}", JsonOptions);
        Assert.Equal(nameof(InvoiceStatus.Void), after!.Invoice.Status);
        await AssertLedgerReconciles();
    }

    [Fact]
    public async Task Concurrent_sales_never_oversell_limited_stock()
    {
        var productId = await NewProductAsync();
        await StockAsync(productId, 5);
        var pharmacist = await UserAsync(Roles.Pharmacist);

        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
            pharmacist.SendAsync(Post("/api/pharmacy/sales", new CreateSaleRequest(null, null, [new SaleLineInput(productId, 1)])))));

        var sold = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        var codes = await Task.WhenAll(responses.Where(r => r.StatusCode != HttpStatusCode.Created).Select(r => r.Content.ReadAsStringAsync()));
        Assert.All(codes, body => Assert.True(body.Contains("insufficient_stock") || body.Contains("busy"), body));
        Assert.InRange(sold, 1, 5);

        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var onHand = await db.StockBatches.Where(b => b.ProductId == productId).SumAsync(b => b.Quantity);
        Assert.Equal(5 - sold, onHand);

        var moved = await db.StockMovements.Where(m => m.Batch!.ProductId == productId).SumAsync(m => m.Quantity);
        Assert.Equal(onHand, moved);
        await AssertLedgerReconciles();
    }

    [Fact]
    public async Task Prescription_sale_updates_dispensing_and_cannot_exceed_the_prescribed_amount()
    {
        var productId = await NewProductAsync();
        await StockAsync(productId, 10);

        var patient = await NewPatientAsync();
        var reception = await UserAsync(Roles.Receptionist);
        var departments = await reception.GetFromJsonAsync<List<LookupItem>>("/api/lookups/departments");
        var visit = await Created<VisitRow>(await reception.SendAsync(Post("/api/visits", new CreateVisitRequest(patient.Id, (int)departments![0].Id, null, null, null, await api.PaidTokenAsync()))));

        var doctor = await UserAsync(Roles.Doctor);
        var rx = await Created<PrescriptionDto>(await doctor.SendAsync(Post("/api/prescriptions", new CreatePrescriptionRequest(visit.Id, null, [new RxItemInput("دوا", null, null, null, null, 2)]))));

        var pharmacist = await UserAsync(Roles.Pharmacist);
        var itemId = rx.Items[0].Id;
        var over = await pharmacist.SendAsync(Post("/api/pharmacy/sales", new CreateSaleRequest(patient.Id, rx.Id, [new SaleLineInput(productId, 3, itemId)])));
        Assert.Contains("over_dispense", await over.Content.ReadAsStringAsync());

        await Created<SaleRow>(await pharmacist.SendAsync(Post("/api/pharmacy/sales", new CreateSaleRequest(patient.Id, rx.Id, [new SaleLineInput(productId, 1, itemId)]))));
        var partial = (await pharmacist.GetFromJsonAsync<List<PrescriptionDto>>("/api/prescriptions?q=" + rx.RxNumber, JsonOptions))!.Single();
        Assert.Equal(nameof(PrescriptionStatus.PartiallyDispensed), partial.Status);

        await Created<SaleRow>(await pharmacist.SendAsync(Post("/api/pharmacy/sales", new CreateSaleRequest(patient.Id, rx.Id, [new SaleLineInput(productId, 1, itemId)]))));
        var full = (await pharmacist.GetFromJsonAsync<List<PrescriptionDto>>("/api/prescriptions?q=" + rx.RxNumber, JsonOptions))!.Single();
        Assert.Equal(nameof(PrescriptionStatus.Dispensed), full.Status);
    }

    [Fact]
    public async Task Standalone_pharmacy_sale_reaches_the_cashier_and_a_paid_sale_cannot_be_returned()
    {
        var productId = await NewProductAsync(salePrice: 250);
        await StockAsync(productId, 4);
        var pharmacist = await UserAsync(Roles.Pharmacist);

        var sale = await Created<SaleRow>(await pharmacist.SendAsync(Post("/api/pharmacy/sales", new CreateSaleRequest(null, null, [new SaleLineInput(productId, 2)]))));
        Assert.Null(sale.PatientId);
        Assert.Equal(500, sale.Balance);

        var cashier = await CashierWithOpenSessionAsync();
        await Created<PaymentRow>(await cashier.SendAsync(Post("/api/payments", new PaymentRequest(sale.InvoiceId, 500, PaymentMethod.Cash, null))));

        var denied = await pharmacist.PostAsJsonAsync($"/api/pharmacy/sales/{sale.Id}/return", new ReturnSaleRequest(sale.Version, "برگشت"));
        Assert.Contains("sale_is_paid", await denied.Content.ReadAsStringAsync());
        await AssertLedgerReconciles();
    }

    [Fact]
    public async Task Returning_an_unpaid_sale_restores_stock_and_reverses_the_money()
    {
        var productId = await NewProductAsync();
        await StockAsync(productId, 3);
        var pharmacist = await UserAsync(Roles.Pharmacist);

        var sale = await Created<SaleRow>(await pharmacist.SendAsync(Post("/api/pharmacy/sales", new CreateSaleRequest(null, null, [new SaleLineInput(productId, 2)]))));
        var returned = await pharmacist.PostAsJsonAsync($"/api/pharmacy/sales/{sale.Id}/return", new ReturnSaleRequest(sale.Version, "اشتباه در فروش"));
        Assert.True(returned.StatusCode == HttpStatusCode.NoContent, await returned.Content.ReadAsStringAsync());

        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(3, await db.StockBatches.Where(b => b.ProductId == productId).SumAsync(b => b.Quantity));
        Assert.Equal(InvoiceStatus.Void, await db.Invoices.Where(i => i.Id == sale.InvoiceId).Select(i => i.Status).SingleAsync());
        await AssertLedgerReconciles();
    }

    [Fact]
    public async Task Expired_batch_is_not_sold_and_earliest_expiry_goes_first()
    {
        var productId = await NewProductAsync();
        var manager = await UserAsync(Roles.Inventory);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var purchase = await manager.SendAsync(Post("/api/inventory/purchases", new CreatePurchaseRequest(null, "pharmacy",
        [
            new PurchaseLineInput(productId, "OLD", today.AddDays(-5), 10, 10),
            new PurchaseLineInput(productId, "LATE", today.AddYears(2), 2, 10),
            new PurchaseLineInput(productId, "SOON", today.AddMonths(2), 2, 10),
        ])));
        Assert.Equal(HttpStatusCode.Created, purchase.StatusCode);

        var pharmacist = await UserAsync(Roles.Pharmacist);
        await Created<SaleRow>(await pharmacist.SendAsync(Post("/api/pharmacy/sales", new CreateSaleRequest(null, null, [new SaleLineInput(productId, 3)]))));

        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var byLot = await db.StockBatches.Where(b => b.ProductId == productId).ToDictionaryAsync(b => b.LotNumber, b => b.Quantity);
        Assert.Equal(10, byLot["OLD"]);
        Assert.Equal(0, byLot["SOON"]);
        Assert.Equal(1, byLot["LATE"]);

        var tooMany = await pharmacist.SendAsync(Post("/api/pharmacy/sales", new CreateSaleRequest(null, null, [new SaleLineInput(productId, 2)])));
        Assert.Contains("insufficient_stock", await tooMany.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Stock_adjustment_needs_approval_right_and_a_reason()
    {
        var productId = await NewProductAsync();
        await StockAsync(productId, 5);

        using var scope = api.Services.CreateScope();
        var batchId = await scope.ServiceProvider.GetRequiredService<AppDbContext>().StockBatches.Where(b => b.ProductId == productId).Select(b => b.Id).SingleAsync();

        var pharmacist = await UserAsync(Roles.Pharmacist);
        var denied = await pharmacist.SendAsync(Post("/api/inventory/adjustments", new AdjustStockRequest(batchId, -1, MovementKind.Adjustment, "شمارش")));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var manager = await UserAsync(Roles.Inventory);
        var below = await manager.SendAsync(Post("/api/inventory/adjustments", new AdjustStockRequest(batchId, -6, MovementKind.Adjustment, "شمارش")));
        Assert.Contains("insufficient_stock", await below.Content.ReadAsStringAsync());

        var ok = await manager.SendAsync(Post("/api/inventory/adjustments", new AdjustStockRequest(batchId, -2, MovementKind.Adjustment, "شمارش فیزیکی")));
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
    }

    [Fact]
    public async Task Standalone_optical_order_moves_to_delivery_and_tracks_the_balance()
    {
        var frameId = await NewProductAsync(salePrice: 1500, kind: ProductKind.Frame);
        await StockAsync(frameId, 1, "optical", 800);
        var optical = await UserAsync(Roles.Optical);

        var order = await Created<OpticalOrderRow>(await optical.SendAsync(Post("/api/optical/orders", new CreateOpticalOrderRequest(null, null, "عینک مطالعه",
            [new SaleLineInput(frameId, 1)], [new CustomLineInput("عدسی", 700)]))));
        Assert.Equal(2200, order.Total);
        Assert.Equal(nameof(OpticalOrderStatus.Ordered), order.Status);

        var cashier = await CashierWithOpenSessionAsync();
        await Created<PaymentRow>(await cashier.SendAsync(Post("/api/payments", new PaymentRequest(order.InvoiceId, 1000, PaymentMethod.Cash, null))));

        var list = await optical.GetFromJsonAsync<PagedResult<OpticalOrderRow>>("/api/optical/orders", JsonOptions);
        var seen = list!.Items.Single(o => o.Id == order.Id);
        Assert.Equal(1200, seen.Balance);

        var moved = await optical.PostAsJsonAsync($"/api/optical/orders/{order.Id}/status", new ChangeOpticalStatusRequest(OpticalOrderStatus.Delivered, seen.Version));
        Assert.Equal(HttpStatusCode.NoContent, moved.StatusCode);
        await AssertLedgerReconciles();
    }

    [Fact]
    public async Task Cash_session_reports_what_was_collected()
    {
        var serviceId = await NewServiceAsync(1000);
        var patient = await NewPatientAsync();
        var invoice = await NewInvoiceAsync(patient.Id, serviceId);
        var cashier = await CashierWithOpenSessionAsync();
        await Created<PaymentRow>(await cashier.SendAsync(Post("/api/payments", new PaymentRequest(invoice.Id, 400, PaymentMethod.Cash, null))));

        var sessions = await cashier.GetFromJsonAsync<List<SessionRow>>("/api/cash/sessions?mine=true", JsonOptions);
        var session = Assert.Single(sessions!);
        Assert.Equal(400, session.CashCollected);

        var closed = await cashier.PostAsJsonAsync($"/api/cash/sessions/{session.Id}/close", new CloseSessionRequest(390, session.Version));
        var after = (await closed.Content.ReadFromJsonAsync<SessionRow>(JsonOptions))!;
        Assert.Equal(400, after.ExpectedAmount);
        Assert.Equal(390, after.CountedAmount);

        var noSession = await cashier.SendAsync(Post("/api/payments", new PaymentRequest(invoice.Id, 10, PaymentMethod.Cash, null)));
        Assert.Contains("no_open_session", await noSession.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Expense_cancel_reverses_and_ledger_reconciles_after_mixed_concurrent_activity()
    {
        var serviceId = await NewServiceAsync(100);
        var patient = await NewPatientAsync();
        var cashier = await CashierWithOpenSessionAsync();
        var accountant = await UserAsync(Roles.Accountant);

        var expense = JsonDocument.Parse(await (await accountant.SendAsync(Post("/api/expenses",
            new CreateExpenseRequest("برق", null, 75.50m, DateOnly.FromDateTime(DateTime.UtcNow))))).Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.NoContent, (await accountant.PostAsJsonAsync($"/api/expenses/{expense}/cancel", new VoidRequest("اشتباه", 0))).StatusCode);

        var invoices = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => NewInvoiceAsync(patient.Id, serviceId)));
        await Task.WhenAll(invoices.Select(i => cashier.SendAsync(Post("/api/payments", new PaymentRequest(i.Id, 100, PaymentMethod.Cash, null)))));

        await AssertLedgerReconciles();

        var view = await accountant.GetFromJsonAsync<ReconciliationResult>("/api/ledger/reconciliation", JsonOptions);
        Assert.True(view!.Balanced);
    }

    [Fact]
    public async Task Clinical_roles_cannot_touch_finance_and_cashier_cannot_read_the_chart()
    {
        var doctor = await UserAsync(Roles.Doctor);
        Assert.Equal(HttpStatusCode.Forbidden, (await doctor.GetAsync("/api/invoices")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await doctor.GetAsync("/api/ledger/balances")).StatusCode);

        var lab = await UserAsync(Roles.Lab);
        Assert.Equal(HttpStatusCode.Forbidden, (await lab.GetAsync("/api/expenses")).StatusCode);
    }
}
