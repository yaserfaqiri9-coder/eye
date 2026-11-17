using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Clinical;
using AlShifa.Api.Features.Finance;
using AlShifa.Api.Features.Inventory;
using AlShifa.Api.Features.Patients;
using AlShifa.Api.Features.Visits;
using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AlShifa.Tests;

[Collection("api")]
public sealed class ReportDocumentTests(ApiFixture api)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly string[] Everyday = ["patients", "doctors", "surgeries", "diagnostics", "diseases"];
    private static readonly string[] Finance = ["cash", "outstanding", "expenses", "profit-loss", "revenue-sources", "expense-summary", "daily-finance", "cash-flow"];

    private static string Name(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..20];

    private async Task<HttpClient> UserAsync(string role)
    {
        var name = Name("u");
        await api.CreateUserAsync(name, role);
        return await api.LoginAsync(name);
    }

    private async Task<T> InDbAsync<T>(Func<AppDbContext, Task<T>> read)
    {
        using var scope = api.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private static async Task<T> Read<T>(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, text);
        return JsonSerializer.Deserialize<T>(text, JsonOptions)!;
    }

    private static async Task<JsonElement> ReportAsync(HttpClient client, string key, string query = "") =>
        await Read<JsonElement>(await client.GetAsync($"/api/reports/{key}{query}"));

    private static decimal Tile(JsonElement report, string label) =>
        report.GetProperty("tiles").EnumerateArray().Single(t => t.GetProperty("label").GetString() == label).GetProperty("value").GetDecimal();

    private static JsonElement Table(JsonElement report, string title) =>
        report.GetProperty("tables").EnumerateArray().Single(t => t.GetProperty("title").GetString() == title);

    private static IEnumerable<JsonElement[]> Rows(JsonElement table) =>
        table.GetProperty("rows").EnumerateArray().Select(r => r.EnumerateArray().ToArray());

    private static string Today => KabulClock.Today.ToString("yyyy-MM-dd");

    private async Task<PatientDto> NewPatientAsync(Gender gender = Gender.Female, int age = 30)
    {
        var desk = await api.FeeDeskAsync();
        return await Read<PatientDto>(await desk.SendAsync(ApiFixture.Keyed("/api/patients",
            new SavePatientRequest(Name("مریض"), null, null, gender, null, age, null, null))), HttpStatusCode.Created);
    }

    private async Task<(PatientDto Patient, VisitRow Visit)> NewVisitAsync(Gender gender = Gender.Female, int age = 30)
    {
        var desk = await api.FeeDeskAsync();
        var patient = await NewPatientAsync(gender, age);
        var visit = await Read<VisitRow>(await desk.SendAsync(ApiFixture.Keyed("/api/visits",
            new CreateVisitRequest(patient.Id, await api.DepartmentIdAsync("triage"), null, null, null, await api.PaidTokenAsync()))), HttpStatusCode.Created);
        return (patient, visit);
    }

    [Fact]
    public async Task Every_report_answers_over_one_day_and_a_long_period_with_one_list_and_at_most_four_figures()
    {
        var manager = await UserAsync(Roles.Manager);
        await NewVisitAsync();

        foreach (var key in Everyday.Concat(Finance).Concat(["sales", "stock"]))
        {
            var report = await ReportAsync(manager, key);
            Assert.False(string.IsNullOrEmpty(report.GetProperty("title").GetString()));
            Assert.False(string.IsNullOrEmpty(report.GetProperty("description").GetString()));
            // One clear list and one row of figures, so no report opens crowded.
            Assert.Equal(1, report.GetProperty("tables").GetArrayLength());
            Assert.True(report.GetProperty("tiles").GetArrayLength() <= 4, $"{key} has more than four tiles");
            Assert.Equal(JsonValueKind.Null, report.GetProperty("trend").ValueKind);

            var longer = await ReportAsync(manager, key, $"?from={KabulClock.Today.AddDays(-400):yyyy-MM-dd}&to={Today}");
            Assert.Equal(JsonValueKind.Array, longer.GetProperty("tiles").ValueKind);
        }
    }

    [Fact]
    public async Task Each_role_reaches_only_the_reports_it_may_see()
    {
        var doctor = await UserAsync(Roles.Doctor);
        foreach (var key in Everyday)
        {
            Assert.Equal(HttpStatusCode.OK, (await doctor.GetAsync($"/api/reports/{key}")).StatusCode);
        }

        foreach (var key in Finance.Concat(["sales", "stock"]))
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await doctor.GetAsync($"/api/reports/{key}")).StatusCode);
        }

        var accountant = await UserAsync(Roles.Accountant);
        foreach (var key in Finance)
        {
            Assert.Equal(HttpStatusCode.OK, (await accountant.GetAsync($"/api/reports/{key}")).StatusCode);
        }

        var triage = await UserAsync(Roles.Triage);
        foreach (var key in Everyday.Concat(Finance).Concat(["sales", "stock"]))
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await triage.GetAsync($"/api/reports/{key}")).StatusCode);
        }

        // Counts only for the reports viewer: a list that names patients needs the right to see them.
        var viewer = await UserAsync(Roles.DoctorReportsViewer);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/reports/doctors")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/reports/diseases")).StatusCode);
        foreach (var key in new[] { "patients", "surgeries", "diagnostics" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync($"/api/reports/{key}")).StatusCode);
        }

        var admin = await UserAsync(Roles.SuperAdmin);
        foreach (var key in Everyday.Concat(Finance).Concat(["sales", "stock"]))
        {
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/reports/{key}")).StatusCode);
        }

        // The reports that were merged away answer no more.
        foreach (var key in new[] { "overview", "income", "adjustments" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/reports/{key}")).StatusCode);
        }
    }

    [Fact]
    public async Task Patients_report_lists_each_visit_with_who_came_and_how_it_ended()
    {
        var reception = await api.FeeDeskAsync();
        var query = $"?from={KabulClock.Today.AddDays(-1):yyyy-MM-dd}&to={Today}";
        var before = await ReportAsync(reception, "patients", query);

        var (old, _) = await NewVisitAsync(Gender.Male, 70);
        await NewVisitAsync(Gender.Female, 10);
        var (gone, cancelled) = await NewVisitAsync(Gender.Female, 45);
        Assert.Equal(HttpStatusCode.OK, (await reception.PostAsJsonAsync($"/api/visits/{cancelled.Id}/status", new ChangeVisitStatusRequest(VisitStatus.Cancelled, cancelled.Version))).StatusCode);

        var after = await ReportAsync(reception, "patients", query);
        Assert.Equal(2, Tile(after, "مراجعات") - Tile(before, "مراجعات"));
        Assert.Equal(2, Tile(after, "مریضان") - Tile(before, "مریضان"));
        Assert.Equal(3, Tile(after, "مریضان جدید") - Tile(before, "مریضان جدید"));
        Assert.Equal(1, Tile(after, "لغو شده") - Tile(before, "لغو شده"));

        var rows = Rows(Table(after, "فهرست مراجعات")).ToList();
        var man = rows.Single(r => r[2].GetString() == old.FileNumber);
        Assert.Equal("مرد", man[4].GetString());
        Assert.Equal(70, man[5].GetInt32());
        Assert.Equal("لغو شده", rows.Single(r => r[2].GetString() == gone.FileNumber)[8].GetString());
    }

    [Fact]
    public async Task Diseases_count_only_finalized_diagnoses_and_name_no_patient()
    {
        var doctor = await UserAsync(Roles.Doctor);
        var disease = Name("آب‌مروارید");
        var (patient, visit) = await NewVisitAsync();
        await api.CompleteVisionAsync(visit.Id);
        var draft = await Read<ConsultationDto>(await doctor.PutAsJsonAsync($"/api/consultations/{visit.Id}",
            new SaveConsultationRequest("یافته", null, null, null, [new DiagnosisInput(EyeSide.OD, disease)], null)));

        Assert.DoesNotContain(Rows(Table(await ReportAsync(doctor, "diseases"), "تشخیص‌ها از بیشتر به کمتر")), r => r[0].GetString() == disease);

        Assert.Equal(HttpStatusCode.OK, (await doctor.PostAsJsonAsync($"/api/consultations/{visit.Id}/finalize", new FinalizeRequest(draft.Version, false))).StatusCode);

        var report = await ReportAsync(doctor, "diseases");
        var row = Rows(Table(report, "تشخیص‌ها از بیشتر به کمتر")).Single(r => r[0].GetString() == disease);
        Assert.Equal(1, row[1].GetInt32());
        Assert.Equal(1, row[2].GetInt32());
        Assert.DoesNotContain(patient.FirstName, report.GetRawText());
        // A short numeric file number may occur inside a date or count; compare complete cells.
        Assert.DoesNotContain(Rows(Table(report, "تشخیص‌ها از بیشتر به کمتر")).SelectMany(r => r),
            cell => cell.ValueKind == JsonValueKind.String && cell.GetString() == patient.FileNumber);
    }

    [Fact]
    public async Task Cash_report_lists_each_receipt_and_its_figures_add_up()
    {
        var accountant = await UserAsync(Roles.Accountant);
        var cashier = await api.FeeCashierAsync();
        var token = await api.NewTokenAsync();
        var before = await ReportAsync(accountant, "cash");

        var payment = await Read<PaymentRow>(await cashier.SendAsync(ApiFixture.Keyed("/api/payments", new PaymentRequest(token.InvoiceId, token.Balance, PaymentMethod.Cash, null, token.InvoiceVersion))), HttpStatusCode.Created);

        var after = await ReportAsync(accountant, "cash");
        Assert.Equal(token.Balance, Tile(after, "مجموع دریافت") - Tile(before, "مجموع دریافت"));
        Assert.Equal(token.Balance, Tile(after, "نقد") - Tile(before, "نقد"));
        Assert.Equal(1, Tile(after, "تعداد رسید") - Tile(before, "تعداد رسید"));
        Assert.Equal(Tile(after, "نقد") + Tile(after, "غیرنقد"), Tile(after, "مجموع دریافت"));

        var row = Rows(Table(after, "فهرست دریافت‌ها")).Single(r => r[1].GetString() == payment.ReceiptNumber);
        Assert.Equal(token.InvoiceNumber, row[2].GetString());
        Assert.Equal("نقد", row[5].GetString());
        Assert.Equal(token.Balance, row[7].GetDecimal());
    }

    [Fact]
    public async Task Outstanding_lists_an_unpaid_invoice_until_it_is_paid()
    {
        var accountant = await UserAsync(Roles.Accountant);
        var cashier = await api.FeeCashierAsync();
        var token = await api.NewTokenAsync();

        var open = Rows(Table(await ReportAsync(accountant, "outstanding"), "فاکتورهای پرداخت‌نشده")).Single(r => r[1].GetString() == token.InvoiceNumber);
        Assert.Equal(token.Balance, open[8].GetDecimal());

        await Read<PaymentRow>(await cashier.SendAsync(ApiFixture.Keyed("/api/payments", new PaymentRequest(token.InvoiceId, token.Balance, PaymentMethod.Cash, null, token.InvoiceVersion))), HttpStatusCode.Created);
        Assert.DoesNotContain(Rows(Table(await ReportAsync(accountant, "outstanding"), "فاکتورهای پرداخت‌نشده")), r => r[1].GetString() == token.InvoiceNumber);
    }

    [Fact]
    public async Task Expenses_report_lists_each_expense_and_totals_it()
    {
        var accountant = await UserAsync(Roles.Accountant);
        var what = Name("برق");
        var before = await ReportAsync(accountant, "expenses");
        await Read<JsonElement>(await accountant.SendAsync(ApiFixture.Keyed("/api/expenses", new CreateExpenseRequest(what, "ماه جاری", 25, KabulClock.Today))), HttpStatusCode.Created);

        var after = await ReportAsync(accountant, "expenses");
        Assert.Equal(25, Tile(after, "مجموع مصارف") - Tile(before, "مجموع مصارف"));
        var row = Rows(Table(after, "فهرست مصارف")).Single(r => r[2].GetString() == what);
        Assert.Equal(25, row[4].GetDecimal());
    }

    [Fact]
    public async Task Stock_report_lists_every_product_with_its_state_problems_first()
    {
        var inventory = await UserAsync(Roles.Inventory);
        var low = "کم " + Name("n");
        var expired = "گذشته " + Name("n");
        var fresh = "تازه " + Name("n");
        await InDbAsync(async db =>
        {
            var location = await db.StockLocations.Select(l => l.Id).FirstAsync();
            var products = new[]
            {
                new Product { Code = Name("L"), Name = low, Kind = ProductKind.Drug, Unit = "عدد", ReorderLevel = 10 },
                new Product { Code = Name("E"), Name = expired, Kind = ProductKind.Drug, Unit = "عدد" },
                new Product { Code = Name("F"), Name = fresh, Kind = ProductKind.Drug, Unit = "عدد" },
            };
            db.Products.AddRange(products);
            await db.SaveChangesAsync();
            db.StockBatches.AddRange(
                new StockBatch { ProductId = products[0].Id, LocationId = location, LotNumber = "L1", Quantity = 4, UnitCost = 2 },
                new StockBatch { ProductId = products[1].Id, LocationId = location, LotNumber = "E1", Quantity = 3, UnitCost = 2, ExpiryDate = KabulClock.Today.AddDays(-1) },
                new StockBatch { ProductId = products[2].Id, LocationId = location, LotNumber = "F1", Quantity = 3, UnitCost = 2, ExpiryDate = KabulClock.Today.AddDays(365) });
            return await db.SaveChangesAsync();
        });

        var report = await ReportAsync(inventory, "stock");
        Assert.Equal(JsonValueKind.Null, report.GetProperty("from").ValueKind);
        var rows = Rows(Table(report, "فهرست اجناس")).ToList();
        string State(string name) => rows.Single(r => r[0].GetString() == name)[8].GetString()!;
        Assert.Equal("رو به ختم", State(low));
        Assert.Equal(4, rows.Single(r => r[0].GetString() == low)[3].GetDecimal());
        Assert.Equal("تاریخ گذشته", State(expired));
        Assert.Equal("عادی", State(fresh));
        Assert.True(rows.FindIndex(r => r[0].GetString() == expired) < rows.FindIndex(r => r[0].GetString() == fresh));
        Assert.True(Tile(report, "تاریخ گذشته یا نزدیک") >= 1);
    }

    [Fact]
    public async Task A_department_sees_only_its_own_sales()
    {
        var optical = await UserAsync(Roles.Optical);
        var pharmacist = await UserAsync(Roles.Pharmacist);

        var opticalReport = await ReportAsync(optical, "sales");
        Assert.DoesNotContain(opticalReport.GetProperty("tiles").EnumerateArray(), t => t.GetProperty("label").GetString() == "فروش دواخانه");
        Assert.DoesNotContain(Rows(Table(opticalReport, "فهرست فروش‌ها")), r => r[2].GetString() == "دواخانه");

        var pharmacyReport = await ReportAsync(pharmacist, "sales");
        Assert.DoesNotContain(pharmacyReport.GetProperty("tiles").EnumerateArray(), t => t.GetProperty("label").GetString() == "فروش عینک");
        Assert.DoesNotContain(Rows(Table(pharmacyReport, "فهرست فروش‌ها")), r => r[2].GetString() == "عینک");
    }

    [Fact]
    public async Task Excel_and_pdf_files_hold_the_report_and_only_who_may_export_gets_them()
    {
        var accountant = await UserAsync(Roles.Accountant);
        var what = Name("کرایه");
        await Read<JsonElement>(await accountant.SendAsync(ApiFixture.Keyed("/api/expenses", new CreateExpenseRequest(what, null, 40, KabulClock.Today))), HttpStatusCode.Created);

        var excel = await accountant.GetAsync("/api/reports/expenses/export?format=xlsx");
        Assert.Equal(HttpStatusCode.OK, excel.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", excel.Content.Headers.ContentType?.MediaType);
        Assert.EndsWith(".xlsx", excel.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        using (var zip = new System.IO.Compression.ZipArchive(new MemoryStream(await excel.Content.ReadAsByteArrayAsync())))
        {
            using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
            var sheet = await reader.ReadToEndAsync();
            Assert.Contains("rightToLeft=\"1\"", sheet);
            Assert.Contains("مصارف", sheet);
            Assert.Contains(what, sheet);
            // Text is never a formula, and an amount is a real number Excel can add up.
            Assert.DoesNotContain("<f>", sheet);
            Assert.Contains("<v>40</v>", sheet);
        }

        var pdf = await accountant.GetAsync("/api/reports/expenses/export?format=pdf");
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString((await pdf.Content.ReadAsByteArrayAsync())[..4]));

        Assert.Equal(HttpStatusCode.BadRequest, (await accountant.GetAsync("/api/reports/expenses/export?format=csv")).StatusCode);

        // A doctor may read the doctors' report but not take it away as a file.
        var doctor = await UserAsync(Roles.Doctor);
        Assert.Equal(HttpStatusCode.Forbidden, (await doctor.GetAsync("/api/reports/doctors/export?format=xlsx")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await doctor.GetAsync("/api/reports/doctors/export?format=pdf")).StatusCode);
    }

    [Fact]
    public async Task New_financial_reports_reconcile_reversals_costs_and_Kabul_boundaries_without_counting_collections_as_income()
    {
        var accountant = await UserAsync(Roles.Accountant);
        var marker = Name("finrep");
        try
        {
        var departmentName = await InDbAsync(async db =>
        {
            var department = await db.Departments.FirstAsync();
            var service = new Service { Code = marker, Name = "خدمت گزارش", DepartmentId = department.Id };
            var invoice = new Invoice { InvoiceNumber = marker, Total = 1000, Items = [new InvoiceItem { Description = "خدمت", UnitPrice = 1000, LineTotal = 1000, RevenueAccount = Accounts.ServiceRevenue }] };
            // ServiceId is assigned below after the service has received its database identifier.
            db.Services.Add(service);
            db.Invoices.Add(invoice);
            var expense = new Expense { ExpenseNumber = marker, Category = "مصرف آزمایشی گزارش", Amount = 80, Status = ExpenseStatus.Cancelled, ExpenseDate = new DateOnly(2000, 1, 1) };
            db.Expenses.Add(expense);
            await db.SaveChangesAsync();
            invoice.Items[0].ServiceId = service.Id;

            var start = new DateTime(2001, 1, 1, 19, 30, 0, DateTimeKind.Utc); // 2 January in Kabul.
            LedgerTransaction Add(DateTime at, string debit, string credit, decimal amount, string source = "ReportTest", long id = 0)
            {
                var transaction = new LedgerTransaction
                {
                    Kind = Name("test"), SourceType = source, SourceId = id, CreatedAt = at, Description = marker,
                    Entries = [new LedgerEntry { AccountCode = debit, Debit = amount }, new LedgerEntry { AccountCode = credit, Credit = amount }],
                };
                db.LedgerTransactions.Add(transaction);
                return transaction;
            }
            Add(start.AddSeconds(-1), Accounts.Cash, Accounts.OpeningBalance, 100);
            Add(start, Accounts.Receivable, Accounts.ServiceRevenue, 1000, "InvoiceItem", invoice.Items[0].Id);
            Add(start.AddHours(1), Accounts.Receivable, Accounts.PharmacyRevenue, 400);
            Add(start.AddHours(1), Accounts.Receivable, Accounts.OpticalRevenue, 200);
            Add(start.AddHours(1), Accounts.Discounts, Accounts.Receivable, 100);
            Add(start.AddHours(1), Accounts.Refunds, Accounts.Cash, 50);
            Add(start.AddHours(1), Accounts.CostOfGoods, Accounts.Inventory, 250);
            var original = Add(start.AddHours(1), Accounts.Expenses, Accounts.Cash, 80, "Expense", expense.Id);
            Add(start.AddHours(1), Accounts.InventoryAdjustments, Accounts.Inventory, 20);
            Add(start.AddHours(1), Accounts.Cash, Accounts.Receivable, 900);
            await db.SaveChangesAsync();
            var reversal = Add(start.AddDays(1), Accounts.Cash, Accounts.Expenses, 80, "Expense", expense.Id);
            reversal.ReversalOfId = original.Id;
            Add(start.AddDays(1), Accounts.Expenses, Accounts.Cash, 40);
            Add(start.AddDays(2), Accounts.Receivable, Accounts.ServiceRevenue, 999);
            await db.SaveChangesAsync();
            return department.Name;
        });

        const string range = "?from=2001-01-02&to=2001-01-03";
        var profit = await ReportAsync(accountant, "profit-loss", range);
        Assert.Equal(1450, Tile(profit, "عواید خالص"));
        Assert.Equal(250, Tile(profit, "قیمت خرید فروش‌ها"));
        Assert.Equal(1140, Tile(profit, "مفاد / ضرر"));
        var revenue = await ReportAsync(accountant, "revenue-sources", range);
        var sourceRows = Rows(Table(revenue, "عواید به تفکیک بخش")).ToList();
        Assert.Equal(1000, sourceRows.Single(r => r[0].GetString() == departmentName)[1].GetDecimal());
        Assert.Equal(1450, sourceRows.Sum(r => r[1].GetDecimal()));
        Assert.Equal(1450, Tile(revenue, "عواید خالص"));
        var expenses = await ReportAsync(accountant, "expense-summary", range);
        Assert.Equal(120, Tile(expenses, "مصارف ثبت‌شده"));
        Assert.Equal(80, Tile(expenses, "برگشت اسناد مصرف"));
        Assert.Equal(40, Tile(expenses, "مصارف خالص"));
        var daily = await ReportAsync(accountant, "daily-finance", range);
        var days = Rows(Table(daily, "عواید، مصارف و مفاد روزانه")).ToList();
        Assert.Equal(2, days.Count);
        Assert.Equal("2001-01-02", days[0][0].GetString());
        Assert.Equal(1100, days[0][5].GetDecimal());
        Assert.Equal(40, days[1][5].GetDecimal());
        Assert.Equal(Tile(profit, "مفاد / ضرر"), days.Sum(r => r[5].GetDecimal()));
        var cash = await ReportAsync(accountant, "cash-flow", range);
        Assert.Equal(100, Tile(cash, "مانده دفتری آغاز"));
        Assert.Equal(980, Tile(cash, "ورودی حساب"));
        Assert.Equal(170, Tile(cash, "خروجی حساب"));
        Assert.Equal(910, Tile(cash, "مانده دفتری پایان"));

        // The original expense is cancelled today, but still belongs in the earlier posting period.
        var firstDay = await ReportAsync(accountant, "expense-summary", "?from=2001-01-02&to=2001-01-02");
        Assert.Equal(80, Tile(firstDay, "مصارف خالص"));
        var empty = await ReportAsync(accountant, "profit-loss", "?from=1999-01-01&to=1999-01-01");
        Assert.Equal(0, Tile(empty, "مفاد / ضرر"));

        foreach (var key in new[] { "profit-loss", "revenue-sources", "expense-summary", "daily-finance", "cash-flow" })
        {
            foreach (var format in new[] { "xlsx", "pdf" })
            {
                var response = await accountant.GetAsync($"/api/reports/{key}/export{range}&format={format}");
                Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
                var bytes = await response.Content.ReadAsByteArrayAsync();
                Assert.True(bytes.Length > 100);
                Assert.Equal(format == "pdf" ? "%PDF" : "PK", System.Text.Encoding.ASCII.GetString(bytes[..(format == "pdf" ? 4 : 2)]));
            }
            Assert.Equal(HttpStatusCode.BadRequest, (await accountant.GetAsync($"/api/reports/{key}?from=2001-01-03&to=2001-01-02")).StatusCode);
        }
        }
        finally
        {
            // Synthetic boundary fixtures must not change later tests' whole-ledger reconciliation.
            await InDbAsync(async db =>
            {
                var transactions = await db.LedgerTransactions.Include(t => t.Entries).Where(t => t.Description == marker).ToListAsync();
                foreach (var transaction in transactions) transaction.ReversalOfId = null;
                await db.SaveChangesAsync();
                db.LedgerEntries.RemoveRange(transactions.SelectMany(t => t.Entries));
                db.LedgerTransactions.RemoveRange(transactions);
                var invoices = await db.Invoices.Include(i => i.Items).Where(i => i.InvoiceNumber == marker).ToListAsync();
                db.InvoiceItems.RemoveRange(invoices.SelectMany(i => i.Items));
                db.Invoices.RemoveRange(invoices);
                db.Expenses.RemoveRange(await db.Expenses.Where(e => e.ExpenseNumber == marker).ToListAsync());
                await db.SaveChangesAsync();
                db.Services.RemoveRange(await db.Services.Where(s => s.Code == marker).ToListAsync());
                await db.SaveChangesAsync();
                return true;
            });
        }
    }

    [Fact]
    public async Task Seeding_a_database_in_use_takes_the_register_permission_back_from_reception()
    {
        using var scope = api.Services.CreateScope();
        var roles = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.RoleManager<AlShifa.Api.Features.Users.AppRole>>();
        var role = (await roles.FindByNameAsync(Roles.Receptionist))!;
        var claim = new System.Security.Claims.Claim(RolePermissions.ClaimType, "cash.create");
        await roles.AddClaimAsync(role, claim);

        await Seeder.SeedReferenceDataAsync(scope.ServiceProvider);

        Assert.DoesNotContain((await roles.GetClaimsAsync(role)).Select(c => c.Value), value => value == "cash.create");
    }

    [Fact]
    public async Task A_register_opened_before_fees_moved_can_still_be_closed_by_reception()
    {
        var name = Name("r");
        await api.CreateUserAsync(name, Roles.Receptionist);
        var reception = await api.LoginAsync(name);
        var sessionId = await InDbAsync(async db =>
        {
            var user = await db.Users.Where(u => u.UserName == name).Select(u => u.Id).SingleAsync();
            var register = new CashRegister { Name = name };
            db.CashRegisters.Add(register);
            await db.SaveChangesAsync();
            var session = new CashSession { RegisterId = register.Id, OpenedByUserId = user, OpeningAmount = 5 };
            db.CashSessions.Add(session);
            await db.SaveChangesAsync();
            return session.Id;
        });

        var sessions = await Read<JsonElement>(await reception.GetAsync("/api/cash/sessions?mine=true"));
        Assert.Equal(sessionId, sessions.EnumerateArray().Single().GetProperty("id").GetInt64());
        Assert.Equal(HttpStatusCode.OK, (await reception.PostAsJsonAsync($"/api/cash/sessions/{sessionId}/close", new CloseSessionRequest(5, 0))).StatusCode);
    }
}
