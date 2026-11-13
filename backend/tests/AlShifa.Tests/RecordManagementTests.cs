using System.Net;
using System.Net.Http.Headers;
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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AlShifa.Tests;

/// <summary>Edit and delete on the directory lists: an unused record goes, one history needs is deactivated.</summary>
[Collection("api")]
public sealed class RecordManagementTests(ApiFixture api)
{
    private static string Unique => Guid.NewGuid().ToString("N")[..12];

    private async Task<HttpClient> User(string role)
    {
        var name = "records_" + Unique;
        await api.CreateUserAsync(name, role);
        return await api.LoginAsync(name);
    }

    private async Task<T> InDb<T>(Func<AppDbContext, Task<T>> action)
    {
        using var scope = api.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private static async Task<bool> Delete(HttpClient client, string path)
    {
        var response = await client.DeleteAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DeleteOutcome>())!.Deleted;
    }

    [Fact]
    public async Task Unused_department_is_deleted_used_one_is_deactivated_and_system_one_is_refused()
    {
        var admin = await User(Roles.SuperAdmin);

        var unused = (await (await admin.PostAsJsonAsync("/api/departments", new SaveDepartmentRequest("بدون استفاده"))).Content.ReadFromJsonAsync<Department>())!;
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/departments/{unused.Id}?version={unused.Version + 1}")).StatusCode);
        Assert.True(await Delete(admin, $"/api/departments/{unused.Id}?version={unused.Version}"));
        Assert.False(await InDb(db => db.Departments.AnyAsync(d => d.Id == unused.Id)));

        var used = (await (await admin.PostAsJsonAsync("/api/departments", new SaveDepartmentRequest("با کارمند"))).Content.ReadFromJsonAsync<Department>())!;
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/staff", new SaveStaffRequest("کارمند " + Unique, null, false, true, used.Id, null))).StatusCode);
        Assert.False(await Delete(admin, $"/api/departments/{used.Id}?version={used.Version}"));
        Assert.False((await InDb(db => db.Departments.SingleAsync(d => d.Id == used.Id))).IsActive);

        var triage = await InDb(db => db.Departments.SingleAsync(d => d.Code == "triage"));
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/departments/{triage.Id}?version={triage.Version}")).StatusCode);
    }

    [Fact]
    public async Task Patient_without_history_is_deleted_with_its_change_log_and_one_with_a_visit_stays()
    {
        var desk = await User(Roles.Receptionist);
        var request = new SavePatientRequest("مریض" + Unique, null, null, Gender.Male, null, 40, null, null, true);
        var patient = (await (await desk.SendAsync(ApiFixture.Keyed("/api/patients", request))).Content.ReadFromJsonAsync<PatientDto>())!;
        var edited = await desk.PutAsJsonAsync($"/api/patients/{patient.Id}", request with { Phone = "0700000001", Version = patient.Version });
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var version = (await edited.Content.ReadFromJsonAsync<PatientDto>())!.Version;

        Assert.True(await Delete(desk, $"/api/patients/{patient.Id}?version={version}"));
        Assert.False(await InDb(db => db.Patients.AnyAsync(p => p.Id == patient.Id)));

        var kept = (await (await desk.SendAsync(ApiFixture.Keyed("/api/patients", request with { FirstName = "مریض" + Unique }))).Content.ReadFromJsonAsync<PatientDto>())!;
        var invoice = await InDb(async db =>
        {
            db.Invoices.Add(new Invoice { InvoiceNumber = "T-" + Unique, PatientId = kept.Id });
            return await db.SaveChangesAsync();
        });
        Assert.Equal(1, invoice);
        var refused = await desk.DeleteAsync($"/api/patients/{kept.Id}?version={kept.Version}");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.True(await InDb(db => db.Patients.AnyAsync(p => p.Id == kept.Id)));
    }

    [Fact]
    public async Task Service_can_be_edited_and_goes_with_its_prices_unless_invoiced()
    {
        var admin = await User(Roles.SuperAdmin);
        var id = await CreateServiceAsync(admin, "خدمت");
        Assert.Matches("^[0-9]{1,4}$", await InDb(db => db.Services.Where(s => s.Id == id).Select(s => s.Code).SingleAsync()));

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/services/{id}", new EditServiceRequest("خدمت تازه", true))).StatusCode);
        Assert.Equal("خدمت تازه", await InDb(db => db.Services.Where(s => s.Id == id).Select(s => s.Name).SingleAsync()));

        Assert.True(await Delete(admin, $"/api/services/{id}"));
        Assert.False(await InDb(db => db.ServicePrices.AnyAsync(p => p.ServiceId == id)));

        // Invoice lines hold the service id without a foreign key, so only the endpoint's own check protects them.
        var billed = await CreateServiceAsync(admin, "خدمت فاکتور");
        var cashier = await User(Roles.Cashier);
        Assert.Equal(HttpStatusCode.Created, (await cashier.SendAsync(ApiFixture.Keyed("/api/invoices", new CreateInvoiceRequest(null, null, [new InvoiceLineInput(billed, 1)])))).StatusCode);
        Assert.False(await Delete(admin, $"/api/services/{billed}"));
        Assert.False((await InDb(db => db.Services.SingleAsync(s => s.Id == billed))).IsActive);
        Assert.True(await InDb(db => db.ServicePrices.AnyAsync(p => p.ServiceId == billed)));

        var fee = await InDb(db => db.Services.SingleAsync(s => s.Code == ServiceCodes.VisitFee));
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/services/{fee.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync($"/api/services/{fee.Id}", new EditServiceRequest(fee.Name, false))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/services/{fee.Id}", new EditServiceRequest(fee.Name, true))).StatusCode);
    }

    [Fact]
    public async Task Register_can_be_renamed_and_one_with_a_session_is_deactivated()
    {
        var admin = await User(Roles.SuperAdmin);
        var created = await admin.PostAsJsonAsync("/api/cash/registers", new SaveRegisterRequest("صندوق " + Unique));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<IdOnly>())!.Id;
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/cash/registers/{id}", new EditRegisterRequest("صندوق تازه", true))).StatusCode);
        Assert.True(await Delete(admin, $"/api/cash/registers/{id}"));

        var usedId = (await (await admin.PostAsJsonAsync("/api/cash/registers", new SaveRegisterRequest("صندوق " + Unique))).Content.ReadFromJsonAsync<IdOnly>())!.Id;
        await InDb(async db =>
        {
            db.CashSessions.Add(new CashSession { RegisterId = usedId, OpenedByUserId = 1, OpeningAmount = 0, ClosedAt = DateTime.UtcNow });
            return await db.SaveChangesAsync();
        });
        Assert.False(await Delete(admin, $"/api/cash/registers/{usedId}"));
        Assert.False((await InDb(db => db.CashRegisters.SingleAsync(r => r.Id == usedId))).IsActive);
    }

    [Fact]
    public async Task Product_with_stock_history_is_deactivated_and_a_clerk_cannot_delete()
    {
        var inventory = await User(Roles.Inventory);
        var pharmacist = await User(Roles.Pharmacist);
        var save = new SaveProductRequest("جنس " + Unique, ProductKind.General, null, "عدد", 0, 10);
        var id = (await (await inventory.PostAsJsonAsync("/api/inventory/products", save)).Content.ReadFromJsonAsync<IdOnly>())!.Id;
        Assert.Equal(HttpStatusCode.Forbidden, (await pharmacist.DeleteAsync($"/api/inventory/products/{id}?version=0")).StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await inventory.SendAsync(ApiFixture.Keyed("/api/inventory/purchases", new CreatePurchaseRequest("تأمین", "central", [new PurchaseLineInput(id, null, null, 1, 5)])))).StatusCode);
        Assert.False(await Delete(inventory, $"/api/inventory/products/{id}?version=0"));
        Assert.False((await InDb(db => db.Products.SingleAsync(p => p.Id == id))).IsActive);

        var unused = (await (await inventory.PostAsJsonAsync("/api/inventory/products", save with { Name = "جنس " + Unique })).Content.ReadFromJsonAsync<IdOnly>())!.Id;
        Assert.True(await Delete(inventory, $"/api/inventory/products/{unused}?version=0"));
    }

    private sealed record IdOnly(int Id);

    private static async Task<int> CreateServiceAsync(HttpClient admin, string name)
    {
        var response = await admin.PostAsJsonAsync("/api/services", new SaveServiceRequest(name, null, true, 100));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<IdOnly>())!.Id;
    }

    private sealed record Created(long Id);

    private async Task<int> NewProduct(HttpClient inventory, decimal price = 10, ProductKind kind = ProductKind.General) =>
        (await (await inventory.PostAsJsonAsync("/api/inventory/products", new SaveProductRequest("جنس " + Unique, kind, null, "عدد", 0, price))).Content.ReadFromJsonAsync<IdOnly>())!.Id;

    private async Task<long> Buy(HttpClient inventory, int product, decimal quantity, decimal cost = 5, string location = "central")
    {
        var response = await inventory.SendAsync(ApiFixture.Keyed("/api/inventory/purchases", new CreatePurchaseRequest("تأمین", location, [new PurchaseLineInput(product, null, null, quantity, cost)])));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Created>())!.Id;
    }

    private Task<decimal> OnHand(int product) =>
        InDb(async db => await db.StockBatches.Where(b => b.ProductId == product).SumAsync(b => b.Quantity));

    private async Task AssertBooksBalance(HttpClient accountant)
    {
        var result = (await accountant.GetFromJsonAsync<ReconciliationResult>("/api/ledger/reconciliation"))!;
        Assert.True(result.Balanced, $"{result}");
    }

    [Fact]
    public async Task Correcting_an_expense_cancels_it_and_records_the_right_one_with_the_books_in_balance()
    {
        var accountant = await User(Roles.Accountant);
        var today = KabulClock.Today;
        var created = await accountant.SendAsync(ApiFixture.Keyed("/api/expenses", new CreateExpenseRequest("برق", null, 100, today)));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<Created>())!.Id;

        var correction = new CorrectExpenseRequest("برق", "ماه جاری", 150, today, "مبلغ اشتباه بود", 0);
        Assert.Equal(HttpStatusCode.Conflict, (await accountant.PostAsJsonAsync($"/api/expenses/{id}/correct", correction with { Version = 9 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await accountant.PostAsJsonAsync($"/api/expenses/{id}/correct", correction)).StatusCode);

        Assert.Equal(ExpenseStatus.Cancelled, (await InDb(db => db.Expenses.SingleAsync(e => e.Id == id))).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await accountant.PostAsJsonAsync($"/api/expenses/{id}/correct", correction with { Version = 1 })).StatusCode);
        await AssertBooksBalance(accountant);
    }

    [Fact]
    public async Task Only_a_standalone_unpaid_invoice_is_corrected_in_place()
    {
        var admin = await User(Roles.SuperAdmin);
        var cashier = await User(Roles.Cashier);
        var accountant = await User(Roles.Accountant);
        var service = await CreateServiceAsync(admin, "خدمت");

        var first = (await (await cashier.SendAsync(ApiFixture.Keyed("/api/invoices", new CreateInvoiceRequest(null, null, [new InvoiceLineInput(service, 1)])))).Content.ReadFromJsonAsync<InvoiceRow>())!;
        var detail = (await cashier.GetFromJsonAsync<InvoiceDetail>($"/api/invoices/{first.Id}"))!;
        Assert.True(detail.Correctable);
        Assert.Equal(service, detail.Items.Single().ServiceId);

        var corrected = await cashier.PostAsJsonAsync($"/api/invoices/{first.Id}/correct", new CorrectInvoiceRequest(null, [new InvoiceLineInput(service, 3)], "تعداد اشتباه بود", first.Version));
        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);
        Assert.Equal(300, (await corrected.Content.ReadFromJsonAsync<InvoiceRow>())!.Total);
        Assert.Equal(InvoiceStatus.Void, (await InDb(db => db.Invoices.SingleAsync(i => i.Id == first.Id))).Status);
        await AssertBooksBalance(accountant);

        // An accountant may approve a discount but neither void nor issue, so the second permission matters.
        Assert.Equal(HttpStatusCode.Forbidden, (await accountant.PostAsJsonAsync($"/api/invoices/{first.Id}/correct", new CorrectInvoiceRequest(null, [new InvoiceLineInput(service, 3)], "تعداد اشتباه بود", first.Version))).StatusCode);

        var inventory = await User(Roles.Inventory);
        var pharmacist = await User(Roles.Pharmacist);
        var product = await NewProduct(inventory, 10, ProductKind.Drug);
        await Buy(inventory, product, 5, 5, "pharmacy");
        var sale = (await (await pharmacist.SendAsync(ApiFixture.Keyed("/api/pharmacy/sales", new CreateSaleRequest(null, null, [new SaleLineInput(product, 1)])))).Content.ReadFromJsonAsync<SaleRow>())!;
        var saleInvoice = (await cashier.GetFromJsonAsync<InvoiceDetail>($"/api/invoices/{sale.InvoiceId}"))!;
        Assert.False(saleInvoice.Correctable);
        Assert.Equal(HttpStatusCode.Conflict, (await cashier.PostAsJsonAsync($"/api/invoices/{sale.InvoiceId}/correct", new CorrectInvoiceRequest(null, [new InvoiceLineInput(service, 1)], "اشتباه بود", saleInvoice.Invoice.Version))).StatusCode);
    }

    [Fact]
    public async Task A_discount_on_an_unpaid_invoice_can_be_taken_back()
    {
        var admin = await User(Roles.SuperAdmin);
        var cashier = await User(Roles.Cashier);
        var accountant = await User(Roles.Accountant);
        var service = await CreateServiceAsync(admin, "خدمت");
        var invoice = (await (await cashier.SendAsync(ApiFixture.Keyed("/api/invoices", new CreateInvoiceRequest(null, null, [new InvoiceLineInput(service, 1)])))).Content.ReadFromJsonAsync<InvoiceRow>())!;

        Assert.Equal(HttpStatusCode.NoContent, (await accountant.PostAsJsonAsync($"/api/invoices/{invoice.Id}/discount", new DiscountRequest(20, "تخفیف آزمایشی", invoice.Version))).StatusCode);
        var discounted = (await accountant.GetFromJsonAsync<InvoiceDetail>($"/api/invoices/{invoice.Id}"))!.Invoice;
        Assert.Equal(20, discounted.Discount);

        // Twice, because the ledger allows one row per kind and source.
        var version = discounted.Version;
        Assert.Equal(HttpStatusCode.NoContent, (await accountant.PostAsJsonAsync($"/api/invoices/{invoice.Id}/discount/cancel", new CancelDiscountRequest("اشتباه ثبت شد", version))).StatusCode);
        version = (await accountant.GetFromJsonAsync<InvoiceDetail>($"/api/invoices/{invoice.Id}"))!.Invoice.Version;
        Assert.Equal(HttpStatusCode.Conflict, (await accountant.PostAsJsonAsync($"/api/invoices/{invoice.Id}/discount/cancel", new CancelDiscountRequest("اشتباه ثبت شد", version))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await accountant.PostAsJsonAsync($"/api/invoices/{invoice.Id}/discount", new DiscountRequest(10, "تخفیف درست", version))).StatusCode);
        version = (await accountant.GetFromJsonAsync<InvoiceDetail>($"/api/invoices/{invoice.Id}"))!.Invoice.Version;
        Assert.Equal(HttpStatusCode.NoContent, (await accountant.PostAsJsonAsync($"/api/invoices/{invoice.Id}/discount/cancel", new CancelDiscountRequest("باز هم اشتباه", version))).StatusCode);

        Assert.Equal(0, (await accountant.GetFromJsonAsync<InvoiceDetail>($"/api/invoices/{invoice.Id}"))!.Invoice.Discount);
        await AssertBooksBalance(accountant);
    }

    [Fact]
    public async Task Cancelling_a_purchase_takes_its_stock_back_unless_some_was_used()
    {
        var inventory = await User(Roles.Inventory);
        var accountant = await User(Roles.Accountant);
        var product = await NewProduct(inventory);
        var id = await Buy(inventory, product, 10);
        Assert.Equal(10, await OnHand(product));

        var detail = (await inventory.GetFromJsonAsync<PurchaseDetail>($"/api/inventory/purchases/{id}"))!;
        Assert.Equal(product, detail.Items.Single().ProductId);

        Assert.Equal(HttpStatusCode.NoContent, (await inventory.PostAsJsonAsync($"/api/inventory/purchases/{id}/cancel", new CancelPurchaseRequest("ثبت اشتباه", detail.Purchase.Version))).StatusCode);
        Assert.Equal(0, await OnHand(product));
        var after = (await inventory.GetFromJsonAsync<PurchaseDetail>($"/api/inventory/purchases/{id}"))!.Purchase;
        Assert.True(after.IsCancelled);
        Assert.Equal(HttpStatusCode.Conflict, (await inventory.PostAsJsonAsync($"/api/inventory/purchases/{id}/cancel", new CancelPurchaseRequest("ثبت اشتباه", after.Version))).StatusCode);
        await AssertBooksBalance(accountant);

        var used = await Buy(inventory, product, 10, 7);
        var batch = await InDb(db => db.StockBatches.Where(b => b.ProductId == product && b.UnitCost == 7).Select(b => b.Id).SingleAsync());
        Assert.Equal(HttpStatusCode.Created, (await inventory.SendAsync(ApiFixture.Keyed("/api/inventory/adjustments", new AdjustStockRequest(batch, -4, MovementKind.Consumption, "مصرف بخش")))).StatusCode);
        var usedVersion = (await inventory.GetFromJsonAsync<PurchaseDetail>($"/api/inventory/purchases/{used}"))!.Purchase.Version;
        var refused = await inventory.PostAsJsonAsync($"/api/inventory/purchases/{used}/cancel", new CancelPurchaseRequest("ثبت اشتباه", usedVersion));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(6, await OnHand(product));
    }

    [Fact]
    public async Task Correcting_a_purchase_replaces_its_stock_and_keeps_a_supplier_deactivated_since()
    {
        var inventory = await User(Roles.Inventory);
        var accountant = await User(Roles.Accountant);
        var product = await NewProduct(inventory);
        var supplier = (await (await inventory.PostAsJsonAsync("/api/suppliers", new SaveSupplierRequest("شرکت " + Unique, null, null))).Content.ReadFromJsonAsync<Supplier>())!;
        var created = await inventory.SendAsync(ApiFixture.Keyed("/api/inventory/purchases", new CreatePurchaseRequest(null, "central", [new PurchaseLineInput(product, null, null, 10, 5)], supplier.Id)));
        var id = (await created.Content.ReadFromJsonAsync<Created>())!.Id;
        Assert.False(await Delete(inventory, $"/api/suppliers/{supplier.Id}?version={supplier.Version}"));

        var version = (await inventory.GetFromJsonAsync<PurchaseDetail>($"/api/inventory/purchases/{id}"))!.Purchase.Version;
        var corrected = await inventory.PostAsJsonAsync($"/api/inventory/purchases/{id}/correct", new CorrectPurchaseRequest(supplier.Id, "central", [new PurchaseLineInput(product, null, null, 8, 5)], "مقدار اشتباه بود", version));
        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);
        Assert.Equal(8, await OnHand(product));
        await AssertBooksBalance(accountant);
    }

    [Fact]
    public async Task Adjustments_and_transfers_can_be_reversed_once_but_a_sale_cannot()
    {
        var inventory = await User(Roles.Inventory);
        var accountant = await User(Roles.Accountant);
        var product = await NewProduct(inventory);
        await Buy(inventory, product, 10);
        var batch = await InDb(db => db.StockBatches.Where(b => b.ProductId == product).Select(b => b.Id).SingleAsync());

        Assert.Equal(HttpStatusCode.Created, (await inventory.SendAsync(ApiFixture.Keyed("/api/inventory/adjustments", new AdjustStockRequest(batch, -3, MovementKind.Adjustment, "شمارش")))).StatusCode);
        Assert.Equal(7, await OnHand(product));
        var adjustment = await InDb(db => db.StockMovements.Where(m => m.BatchId == batch && m.Kind == MovementKind.Adjustment).Select(m => m.Id).SingleAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await inventory.PostAsJsonAsync($"/api/inventory/movements/{adjustment}/reverse", new ReverseMovementRequest("شمارش اشتباه"))).StatusCode);
        Assert.Equal(10, await OnHand(product));
        Assert.Equal(HttpStatusCode.Conflict, (await inventory.PostAsJsonAsync($"/api/inventory/movements/{adjustment}/reverse", new ReverseMovementRequest("شمارش اشتباه"))).StatusCode);
        await AssertBooksBalance(accountant);

        Assert.Equal(HttpStatusCode.Created, (await inventory.SendAsync(ApiFixture.Keyed("/api/inventory/transfers", new TransferStockRequest(batch, "pharmacy", 4)))).StatusCode);
        var pharmacyBatch = await InDb(db => db.StockBatches.Where(b => b.ProductId == product && b.Id != batch).Select(b => b.Id).SingleAsync());
        var transfer = await InDb(db => db.StockMovements.Where(m => m.BatchId == batch && m.Kind == MovementKind.TransferOut).Select(m => m.Id).SingleAsync());
        var listed = (await inventory.GetFromJsonAsync<PagedResult<MovementRow>>($"/api/inventory/movements?productId={product}"))!.Items;
        Assert.True(listed.Single(m => m.Id == transfer).Reversible);
        Assert.False(listed.Single(m => m.Id == adjustment).Reversible);
        Assert.False(listed.Single(m => m.Kind == nameof(MovementKind.Purchase)).Reversible);

        Assert.Equal(HttpStatusCode.NoContent, (await inventory.PostAsJsonAsync($"/api/inventory/movements/{transfer}/reverse", new ReverseMovementRequest("مقصد اشتباه"))).StatusCode);
        Assert.Equal(10, (await InDb(db => db.StockBatches.SingleAsync(b => b.Id == batch))).Quantity);
        Assert.Equal(0, (await InDb(db => db.StockBatches.SingleAsync(b => b.Id == pharmacyBatch))).Quantity);

        var purchaseMovement = listed.Single(m => m.Kind == nameof(MovementKind.Purchase)).Id;
        Assert.Equal(HttpStatusCode.Conflict, (await inventory.PostAsJsonAsync($"/api/inventory/movements/{purchaseMovement}/reverse", new ReverseMovementRequest("اشتباه"))).StatusCode);
    }

    [Fact]
    public async Task A_stock_location_shows_only_its_own_stock_and_history()
    {
        var inventory = await User(Roles.Inventory);
        var product = await NewProduct(inventory);
        await Buy(inventory, product, 10);
        var batch = await InDb(db => db.StockBatches.Where(b => b.ProductId == product).Select(b => b.Id).SingleAsync());
        Assert.Equal(HttpStatusCode.Created, (await inventory.SendAsync(ApiFixture.Keyed("/api/inventory/transfers", new TransferStockRequest(batch, "pharmacy", 4)))).StatusCode);

        var locations = (await inventory.GetFromJsonAsync<List<LocationSummary>>("/api/inventory/locations"))!;
        Assert.Contains(locations, l => l.Code == "central" && l.ProductCount > 0 && l.LastMovementAt != null);
        Assert.Contains(locations, l => l.Code == "optical");

        async Task<LocationStockRow> Stock(string code) =>
            (await inventory.GetFromJsonAsync<LocationDetail>($"/api/inventory/locations/{code}"))!.Stock.Single(s => s.ProductId == product);
        var central = await Stock("central");
        Assert.Equal(6, central.Quantity);
        Assert.Equal(30, central.Value);
        Assert.Equal(4, (await Stock("pharmacy")).Quantity);

        async Task<List<MovementRow>> Moves(string query) =>
            (await inventory.GetFromJsonAsync<PagedResult<MovementRow>>($"/api/inventory/movements?productId={product}&{query}"))!.Items.ToList();
        Assert.Equal(nameof(MovementKind.Purchase), Assert.Single(await Moves("location=central&direction=in")).Kind);
        var outgoing = Assert.Single(await Moves("location=central&direction=out"));
        Assert.Equal(nameof(MovementKind.TransferOut), outgoing.Kind);
        Assert.Equal(-4, outgoing.Quantity);
        Assert.Equal(nameof(MovementKind.TransferIn), Assert.Single(await Moves("location=pharmacy")).Kind);
        Assert.Empty(await Moves("location=optical"));

        Assert.Equal(HttpStatusCode.NotFound, (await inventory.GetAsync("/api/inventory/locations/nowhere")).StatusCode);
        var receptionist = await User(Roles.Receptionist);
        Assert.Equal(HttpStatusCode.Forbidden, (await receptionist.GetAsync("/api/inventory/locations")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await receptionist.GetAsync("/api/inventory/locations/central")).StatusCode);
    }

    [Fact]
    public async Task Correcting_a_sale_returns_its_stock_and_sells_the_right_amount()
    {
        var inventory = await User(Roles.Inventory);
        var pharmacist = await User(Roles.Pharmacist);
        var accountant = await User(Roles.Accountant);
        var product = await NewProduct(inventory, 10, ProductKind.Drug);
        await Buy(inventory, product, 10, 5, "pharmacy");
        var sale = (await (await pharmacist.SendAsync(ApiFixture.Keyed("/api/pharmacy/sales", new CreateSaleRequest(null, null, [new SaleLineInput(product, 4)])))).Content.ReadFromJsonAsync<SaleRow>())!;

        // An unpaid bill only reserves; nothing has left the shelf.
        Assert.Equal(10, await OnHand(product));

        var detail = (await pharmacist.GetFromJsonAsync<SaleDetail>($"/api/pharmacy/sales/{sale.Id}"))!;
        Assert.Equal(4, detail.Items.Single().Quantity);

        var corrected = await pharmacist.PostAsJsonAsync($"/api/pharmacy/sales/{sale.Id}/correct", new CorrectSaleRequest([new SaleLineInput(product, 2)], "مقدار اشتباه بود", detail.Version));
        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);
        var replacement = (await corrected.Content.ReadFromJsonAsync<SaleRow>())!;
        Assert.Equal(20, replacement.Total);
        Assert.Equal(10, await OnHand(product));
        await api.PayAndDeliverAsync(pharmacist, replacement.Id);
        Assert.Equal(8, await OnHand(product));
        Assert.True((await InDb(db => db.Sales.SingleAsync(s => s.Id == sale.Id))).IsReturned);
        await AssertBooksBalance(accountant);
    }

    [Fact]
    public async Task Correcting_an_optical_order_cancels_it_and_keeps_the_custom_lines_on_the_new_one()
    {
        var inventory = await User(Roles.Inventory);
        var optical = await User(Roles.Optical);
        var accountant = await User(Roles.Accountant);
        var frame = await NewProduct(inventory, 50, ProductKind.Frame);
        await Buy(inventory, frame, 5, 20, "optical");
        var created = await optical.SendAsync(ApiFixture.Keyed("/api/optical/orders", new CreateOpticalOrderRequest(null, null, "فریم", [new SaleLineInput(frame, 1)], [new CustomLineInput("ساخت عدسی", 30)])));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var order = (await created.Content.ReadFromJsonAsync<OpticalOrderRow>())!;

        var detail = (await optical.GetFromJsonAsync<OpticalOrderDetail>($"/api/optical/orders/{order.Id}"))!;
        Assert.Equal(1, detail.Sale.Items.Single().Quantity);
        Assert.Equal(30, detail.Sale.CustomLines.Single().Amount);

        var corrected = await optical.PostAsJsonAsync($"/api/optical/orders/{order.Id}/correct", new CorrectOpticalOrderRequest("فریم دیگر", [new SaleLineInput(frame, 2)], [new CustomLineInput("ساخت عدسی", 40)], "مقدار اشتباه بود", detail.Version));
        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);
        Assert.Equal(140, (await corrected.Content.ReadFromJsonAsync<OpticalOrderRow>())!.Total);
        Assert.Equal(3, await OnHand(frame));
        Assert.Equal(OpticalOrderStatus.Cancelled, (await InDb(db => db.OpticalOrders.SingleAsync(o => o.Id == order.Id))).Status);
        await AssertBooksBalance(accountant);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static async Task<T> Read<T>(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.Created)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"{response.StatusCode}: {text}");
        return JsonSerializer.Deserialize<T>(text, JsonOptions)!;
    }

    /// <summary>A paid visit past Vision, ready for the doctor.</summary>
    private async Task<(HttpClient Doctor, VisitRow Visit)> ReadyVisit(string department = "داکتر")
    {
        var reception = await User(Roles.Receptionist);
        var patient = await Read<PatientDto>(await reception.SendAsync(ApiFixture.Keyed("/api/patients", new SavePatientRequest("مریض" + Unique, null, null, Gender.Male, null, 30, null, null, true))));
        var departments = await reception.GetFromJsonAsync<List<LookupItem>>("/api/lookups/departments");
        var visit = await Read<VisitRow>(await reception.SendAsync(ApiFixture.Keyed("/api/visits", new CreateVisitRequest(patient.Id, (int)departments!.Single(d => d.Name == department).Id, null, null, null, await api.PaidTokenAsync()))));
        if (department != "معاینه ابتدایی")
        {
            await api.CompleteVisionAsync(visit.Id);
            visit = (await reception.GetFromJsonAsync<VisitRow>($"/api/visits/{visit.Id}", JsonOptions))!;
        }

        return (await User(Roles.Doctor), visit);
    }

    private static RxItemInput Drug(string name, decimal quantity = 2) => new(name, "1 قطره", null, null, null, quantity);

    private async Task<ConsultationDto> FinalizeExam(HttpClient doctor, long visitId)
    {
        var chart = (await doctor.GetFromJsonAsync<ChartDto>($"/api/consultations/{visitId}/chart", JsonOptions))!;
        var consult = chart.Consultation ?? await Read<ConsultationDto>(await doctor.PutAsJsonAsync($"/api/consultations/{visitId}", new SaveConsultationRequest("یافته", null, null, null, null, null)), HttpStatusCode.OK);
        return await Read<ConsultationDto>(await doctor.PostAsJsonAsync($"/api/consultations/{visitId}/finalize", new FinalizeRequest(consult.Version, false)), HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_prescription_is_edited_freely_until_final_then_only_with_a_reason_and_never_once_dispensed()
    {
        var (doctor, visit) = await ReadyVisit();
        Assert.Equal(HttpStatusCode.OK, (await doctor.PutAsJsonAsync($"/api/consultations/{visit.Id}", new SaveConsultationRequest("یافته", null, null, null, null, null))).StatusCode);
        var rx = await Read<PrescriptionDto>(await doctor.SendAsync(ApiFixture.Keyed("/api/prescriptions", new CreatePrescriptionRequest(visit.Id, null, [Drug("تیمولول")]))));

        var edited = await Read<PrescriptionDto>(await doctor.PutAsJsonAsync($"/api/prescriptions/{rx.Id}", new EditPrescriptionRequest("با غذا", [Drug("لاتانوپروست", 3)], rx.Version)), HttpStatusCode.OK);
        Assert.Equal("لاتانوپروست", edited.Items.Single().DrugName);
        Assert.Equal(HttpStatusCode.Conflict, (await doctor.PutAsJsonAsync($"/api/prescriptions/{rx.Id}", new EditPrescriptionRequest(null, [Drug("کهنه")], rx.Version))).StatusCode);

        var chart = (await doctor.GetFromJsonAsync<ChartDto>($"/api/consultations/{visit.Id}/chart", JsonOptions))!;
        Assert.Contains(chart.Amendments, a => a.Field == "Items" && a.OldValue!.Contains("تیمولول") && a.NewValue!.Contains("لاتانوپروست") && a.Reason == Amendments.BeforeFinal);

        await FinalizeExam(doctor, visit.Id);
        var noReason = new EditPrescriptionRequest("با غذا", [Drug("دورزولامید")], edited.Version);
        Assert.Equal(HttpStatusCode.Conflict, (await doctor.PutAsJsonAsync($"/api/prescriptions/{rx.Id}", noReason)).StatusCode);
        edited = await Read<PrescriptionDto>(await doctor.PutAsJsonAsync($"/api/prescriptions/{rx.Id}", noReason with { Reason = "دوا اشتباه بود" }), HttpStatusCode.OK);
        chart = (await doctor.GetFromJsonAsync<ChartDto>($"/api/consultations/{visit.Id}/chart", JsonOptions))!;
        Assert.Contains(chart.Amendments, a => a.Reason == "دوا اشتباه بود" && a.NewValue!.Contains("دورزولامید"));

        // Once a sale points at the prescription, its lines stay.
        var inventory = await User(Roles.Inventory);
        var pharmacist = await User(Roles.Pharmacist);
        var product = await NewProduct(inventory, 10, ProductKind.Drug);
        await Buy(inventory, product, 5, 5, "pharmacy");
        var item = edited.Items.Single();
        await InDb(db => db.Products.Where(p => p.Id == product).ExecuteUpdateAsync(u => u.SetProperty(p => p.Name, item.DrugName)));
        Assert.Equal(HttpStatusCode.Created, (await pharmacist.SendAsync(ApiFixture.Keyed("/api/pharmacy/sales", new CreateSaleRequest(visit.PatientId, rx.Id, [new SaleLineInput(product, 1, item.Id)])))).StatusCode);
        var dispensed = (await doctor.GetFromJsonAsync<ChartDto>($"/api/consultations/{visit.Id}/chart", JsonOptions))!.Prescriptions.Single();
        Assert.Equal(HttpStatusCode.Conflict, (await doctor.PutAsJsonAsync($"/api/prescriptions/{rx.Id}", new EditPrescriptionRequest(null, [Drug("دیگر")], dispensed.Version, "دلیل"))).StatusCode);
    }

    [Fact]
    public async Task Optical_prescription_edits_stop_when_an_order_is_placed()
    {
        var (doctor, visit) = await ReadyVisit();
        var optical = await User(Roles.Optical);
        var rx = await Read<OpticalRxDto>(await doctor.SendAsync(ApiFixture.Keyed("/api/optical-prescriptions", new CreateOpticalRxRequest(visit.Id, "-1.00", null, null, null, "-1.25", null, null, null, "62", null))));
        var change = new EditOpticalRxRequest("-2.00", null, null, null, "-1.25", null, null, null, "62", null, rx.Version);
        var edited = await Read<OpticalRxDto>(await doctor.PutAsJsonAsync($"/api/optical-prescriptions/{rx.Id}", change), HttpStatusCode.OK);
        Assert.Equal("-2.00", edited.SphereOd);

        Assert.Equal(HttpStatusCode.Created, (await optical.SendAsync(ApiFixture.Keyed("/api/optical/orders", new CreateOpticalOrderRequest(visit.PatientId, rx.Id, "عینک", [], [new CustomLineInput("ساخت", 10)])))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await doctor.PutAsJsonAsync($"/api/optical-prescriptions/{rx.Id}", change with { Version = edited.Version, SphereOd = "-3.00" })).StatusCode);
    }

    [Fact]
    public async Task A_lab_result_can_be_amended_with_a_reason_and_the_old_text_stays()
    {
        var (doctor, visit) = await ReadyVisit();
        var admin = await User(Roles.SuperAdmin);
        var lab = await User(Roles.Lab);
        var testId = (await Read<IdOnly>(await admin.PostAsJsonAsync("/api/diagnostic-tests", new SaveTestRequest("آزمایش " + Unique, true)))).Id;
        var order = await Read<DiagnosticOrderDto>(await doctor.SendAsync(ApiFixture.Keyed("/api/diagnostic-orders", new CreateDiagnosticOrderRequest(visit.PatientId, visit.Id, testId, "اول"))));

        Assert.Equal(HttpStatusCode.NoContent, (await doctor.PutAsJsonAsync($"/api/diagnostic-orders/{order.Id}/note", new EditOrderNoteRequest("دوم", order.Version))).StatusCode);
        order = (await lab.GetFromJsonAsync<DiagnosticOrderDto>($"/api/diagnostic-orders/{order.Id}", JsonOptions))!;
        Assert.Equal("دوم", order.Note);

        var amend = new AmendResultRequest("جواب درست", "جواب اشتباه ثبت شد", order.Version);
        Assert.Equal(HttpStatusCode.Conflict, (await lab.PutAsJsonAsync($"/api/diagnostic-orders/{order.Id}/result", amend)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await lab.PostAsJsonAsync($"/api/diagnostic-orders/{order.Id}/result", new SaveResultRequest("جواب اشتباه", order.Version))).StatusCode);
        order = (await lab.GetFromJsonAsync<DiagnosticOrderDto>($"/api/diagnostic-orders/{order.Id}", JsonOptions))!;
        Assert.Equal(HttpStatusCode.Conflict, (await doctor.PutAsJsonAsync($"/api/diagnostic-orders/{order.Id}/note", new EditOrderNoteRequest("سوم", order.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await doctor.PutAsJsonAsync($"/api/diagnostic-orders/{order.Id}/result", amend with { Version = order.Version })).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await lab.PutAsJsonAsync($"/api/diagnostic-orders/{order.Id}/result", amend with { Version = order.Version })).StatusCode);
        var after = (await lab.GetFromJsonAsync<DiagnosticOrderDto>($"/api/diagnostic-orders/{order.Id}", JsonOptions))!;
        Assert.Equal("جواب درست", after.ResultText);
        Assert.NotEqual(order.Version, after.Version);

        var chart = (await doctor.GetFromJsonAsync<ChartDto>($"/api/consultations/{visit.Id}/chart", JsonOptions))!;
        Assert.Contains(chart.Amendments, a => a.OldValue == "جواب اشتباه" && a.NewValue == "جواب درست" && a.Reason == "جواب اشتباه ثبت شد");
    }

    [Fact]
    public async Task A_completed_pre_exam_changes_only_with_a_reason()
    {
        var (doctor, visit) = await ReadyVisit("معاینه ابتدایی");
        var triage = await User(Roles.Triage);
        Assert.Equal(HttpStatusCode.OK, (await triage.PutAsJsonAsync($"/api/pre-examinations/{visit.Id}", new SavePreExamRequest("تاری", null, "6/9", null, "15", null, null, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await triage.PostAsync($"/api/pre-examinations/{visit.Id}/complete", null)).StatusCode);

        var saved = (await triage.GetFromJsonAsync<PreExamDto>($"/api/pre-examinations/{visit.Id}", JsonOptions))!;
        var change = new SavePreExamRequest("تاری", null, "6/12", null, "15", null, null, null, saved.Version);
        Assert.Equal(HttpStatusCode.Conflict, (await triage.PutAsJsonAsync($"/api/pre-examinations/{visit.Id}", change)).StatusCode);
        var amended = await Read<PreExamDto>(await triage.PutAsJsonAsync($"/api/pre-examinations/{visit.Id}", change with { AmendReason = "اشتباه خوانده شد" }), HttpStatusCode.OK);
        Assert.Equal("6/12", amended.VisualAcuityOd);
        Assert.NotNull(amended.CompletedAt);

        var chart = (await doctor.GetFromJsonAsync<ChartDto>($"/api/consultations/{visit.Id}/chart", JsonOptions))!;
        var amendment = Assert.Single(chart.Amendments);
        Assert.Equal(("VisualAcuityOd", "6/9", "6/12"), (amendment.Field, amendment.OldValue, amendment.NewValue));
    }

    [Fact]
    public async Task A_follow_up_can_be_edited_and_deleted_only_by_the_responsible_doctor()
    {
        var (doctor, visit) = await ReadyVisit();
        Assert.Equal(HttpStatusCode.OK, (await doctor.PutAsJsonAsync($"/api/consultations/{visit.Id}", new SaveConsultationRequest("یافته", null, null, null, null, null))).StatusCode);
        var today = KabulClock.Today;
        var created = await Read<FollowUpDto>(await doctor.PostAsJsonAsync("/api/follow-ups", new CreateFollowUpRequest(visit.Id, today.AddDays(7), "اول")));

        var other = await User(Roles.Doctor);
        Assert.Equal(HttpStatusCode.Forbidden, (await other.DeleteAsync($"/api/follow-ups/{created.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await doctor.PutAsJsonAsync($"/api/follow-ups/{created.Id}", new EditFollowUpRequest(today.AddDays(-1), "گذشته"))).StatusCode);
        var edited = await Read<FollowUpDto>(await doctor.PutAsJsonAsync($"/api/follow-ups/{created.Id}", new EditFollowUpRequest(today.AddDays(9), "دوم")), HttpStatusCode.OK);
        Assert.Equal("دوم", edited.Note);

        Assert.Equal(HttpStatusCode.NoContent, (await doctor.DeleteAsync($"/api/follow-ups/{created.Id}")).StatusCode);
        Assert.False(await InDb(db => db.FollowUps.AnyAsync(f => f.Id == created.Id)));
    }

    [Fact]
    public async Task A_surgery_can_change_eye_only_before_it_starts_and_a_closed_case_needs_a_reason()
    {
        var (doctor, visit) = await ReadyVisit();
        var admin = await User(Roles.SuperAdmin);
        var service = await CreateServiceAsync(admin, "خدمت عملیات");
        var surgery = await Read<SurgeryRow>(await doctor.SendAsync(ApiFixture.Keyed("/api/surgeries", new CreateSurgeryRequest(visit.Id, "کتاراکت", EyeSide.OD, service))));

        var edit = new SaveSurgeryRequest(null, null, false, null, null, surgery.Version, "پیوند قرنیه", EyeSide.OS);
        Assert.Equal(HttpStatusCode.NoContent, (await doctor.PutAsJsonAsync($"/api/surgeries/{surgery.Id}", edit)).StatusCode);
        var saved = await InDb(db => db.SurgeryCases.SingleAsync(c => c.Id == surgery.Id));
        Assert.Equal((EyeSide.OS, "پیوند قرنیه"), (saved.Eye, saved.SurgeryType));
        Assert.Equal("پیوند قرنیه", await InDb(db => db.InvoiceItems.Where(i => i.SourceType == nameof(SurgeryCase) && i.SourceId == surgery.Id).Select(i => i.Description).SingleAsync()));

        // Force the later states directly: paying and running the whole case is covered elsewhere.
        await InDb(async db =>
        {
            var row = await db.SurgeryCases.SingleAsync(c => c.Id == surgery.Id);
            row.Status = SurgeryStatus.InProgress;
            return await db.SaveChangesAsync();
        });
        var version = (await InDb(db => db.SurgeryCases.SingleAsync(c => c.Id == surgery.Id))).Version;
        Assert.Equal(HttpStatusCode.Conflict, (await doctor.PutAsJsonAsync($"/api/surgeries/{surgery.Id}", edit with { Version = version, Eye = EyeSide.OD })).StatusCode);

        await InDb(async db =>
        {
            var row = await db.SurgeryCases.SingleAsync(c => c.Id == surgery.Id);
            row.Status = SurgeryStatus.Closed;
            row.OperationReport = "گزارش اول";
            return await db.SaveChangesAsync();
        });
        version = (await InDb(db => db.SurgeryCases.SingleAsync(c => c.Id == surgery.Id))).Version;
        var report = new SaveSurgeryRequest(null, null, false, "گزارش درست", null, version);
        Assert.Equal(HttpStatusCode.Conflict, (await doctor.PutAsJsonAsync($"/api/surgeries/{surgery.Id}", report)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await doctor.PutAsJsonAsync($"/api/surgeries/{surgery.Id}", report with { AmendReason = "گزارش ناقص بود" })).StatusCode);
        var chart = (await doctor.GetFromJsonAsync<ChartDto>($"/api/consultations/{visit.Id}/chart", JsonOptions))!;
        Assert.Contains(chart.Amendments, a => a.OldValue == "گزارش اول" && a.NewValue == "گزارش درست" && a.Reason == "گزارش ناقص بود");
    }

    [Fact]
    public async Task An_appointment_can_be_moved_until_check_in_and_cannot_take_a_booked_slot()
    {
        var desk = await User(Roles.Receptionist);
        var patient = await Read<PatientDto>(await desk.SendAsync(ApiFixture.Keyed("/api/patients", new SavePatientRequest("مریض" + Unique, null, null, Gender.Male, null, 30, null, null, true))));
        var admin = await User(Roles.SuperAdmin);
        var doctorName = "داکتر " + Unique;
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/staff", new SaveStaffRequest(doctorName, null, true, true, null, null))).StatusCode);
        var doctorId = await InDb(db => db.Staff.Where(s => s.FullName == doctorName).Select(s => s.Id).SingleAsync());

        var at = DateTime.UtcNow.AddDays(3).Date.AddHours(6);
        var first = await Read<Created>(await desk.SendAsync(ApiFixture.Keyed("/api/appointments", new CreateAppointmentRequest(patient.Id, doctorId, at, null))));
        var second = await Read<Created>(await desk.SendAsync(ApiFixture.Keyed("/api/appointments", new CreateAppointmentRequest(patient.Id, doctorId, at.AddHours(1), null))));

        Assert.Equal(HttpStatusCode.Conflict, (await desk.PutAsJsonAsync($"/api/appointments/{second.Id}", new EditAppointmentRequest(patient.Id, doctorId, at, null, 0))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await desk.PutAsJsonAsync($"/api/appointments/{second.Id}", new EditAppointmentRequest(patient.Id, doctorId, DateTime.UtcNow.AddDays(-1), null, 0))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await desk.PutAsJsonAsync($"/api/appointments/{second.Id}", new EditAppointmentRequest(patient.Id, doctorId, at.AddHours(2), "جابه‌جا شد", 0))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await desk.PutAsJsonAsync($"/api/appointments/{second.Id}", new EditAppointmentRequest(patient.Id, doctorId, at.AddHours(3), null, 0))).StatusCode);

        var saved = await InDb(db => db.Appointments.SingleAsync(a => a.Id == second.Id));
        Assert.Equal(("جابه‌جا شد", at.AddHours(2)), (saved.Note, saved.ScheduledAt));
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task A_visit_reason_and_a_user_name_can_be_corrected()
    {
        var (_, visit) = await ReadyVisit();
        var desk = await User(Roles.Receptionist);
        var edited = await Read<VisitRow>(await desk.PutAsJsonAsync($"/api/visits/{visit.Id}/reason", new EditVisitReasonRequest("دلیل درست", visit.Version)), HttpStatusCode.OK);
        Assert.Equal("دلیل درست", edited.Reason);
        Assert.Equal(HttpStatusCode.Conflict, (await desk.PutAsJsonAsync($"/api/visits/{visit.Id}/reason", new EditVisitReasonRequest("کهنه", visit.Version))).StatusCode);

        var admin = await User(Roles.SuperAdmin);
        var name = "records_" + Unique;
        await api.CreateUserAsync(name, Roles.Lab);
        var id = await InDb(db => db.Users.Where(u => u.UserName == name).Select(u => u.Id).SingleAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await desk.PutAsJsonAsync($"/api/users/{id}", new EditUserRequest("نام تازه"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/users/{id}", new EditUserRequest("نام تازه"))).StatusCode);
        Assert.Equal("نام تازه", await InDb(db => db.Users.Where(u => u.Id == id).Select(u => u.FullName).SingleAsync()));
    }

    private static MultipartFormDataContent PdfUpload(string ownerType, long ownerId)
    {
        var file = new ByteArrayContent("%PDF-1.4 test"u8.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        return new MultipartFormDataContent { { new StringContent(ownerType), "ownerType" }, { new StringContent(ownerId.ToString()), "ownerId" }, { file, "file", "result.pdf" } };
    }

    [Fact]
    public async Task An_attachment_goes_only_until_its_record_is_final()
    {
        var (doctor, visit) = await ReadyVisit();
        var admin = await User(Roles.SuperAdmin);
        var lab = await User(Roles.Lab);
        var testId = (await Read<IdOnly>(await admin.PostAsJsonAsync("/api/diagnostic-tests", new SaveTestRequest("آزمایش " + Unique, true)))).Id;
        var order = await Read<DiagnosticOrderDto>(await doctor.SendAsync(ApiFixture.Keyed("/api/diagnostic-orders", new CreateDiagnosticOrderRequest(visit.PatientId, visit.Id, testId, null))));

        var removable = await Read<AttachmentRow>(await lab.PostAsync("/api/attachments", PdfUpload("DiagnosticOrder", order.Id)));
        Assert.Equal(HttpStatusCode.Forbidden, (await doctor.DeleteAsync($"/api/attachments/{removable.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await lab.DeleteAsync($"/api/attachments/{removable.Id}")).StatusCode);
        Assert.False(await InDb(db => db.Attachments.AnyAsync(a => a.Id == removable.Id)));

        var kept = await Read<AttachmentRow>(await lab.PostAsync("/api/attachments", PdfUpload("DiagnosticOrder", order.Id)));
        Assert.Equal(HttpStatusCode.NoContent, (await lab.PostAsJsonAsync($"/api/diagnostic-orders/{order.Id}/result", new SaveResultRequest("جواب", order.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await lab.DeleteAsync($"/api/attachments/{kept.Id}")).StatusCode);
    }
}
