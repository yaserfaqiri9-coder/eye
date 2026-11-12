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

    private static readonly string[] Everyday = ["overview", "patients", "doctors", "diagnostics", "surgeries", "diseases"];
    private static readonly string[] Finance = ["income", "cash", "outstanding", "adjustments", "expenses"];

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
    public async Task Every_report_answers_over_one_day_and_a_long_period()
    {
        var manager = await UserAsync(Roles.Manager);
        await NewVisitAsync();

        foreach (var key in Everyday.Concat(Finance).Concat(["sales", "stock"]))
        {
            var report = await ReportAsync(manager, key);
            Assert.False(string.IsNullOrEmpty(report.GetProperty("title").GetString()));
            Assert.False(string.IsNullOrEmpty(report.GetProperty("description").GetString()));

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

        // The Super Admin holds every role's permissions: every report opens, money included.
        var admin = await UserAsync(Roles.SuperAdmin);
        foreach (var key in Everyday.Concat(Finance).Concat(["sales", "stock"]))
        {
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/api/reports/{key}")).StatusCode);
        }

        Assert.Contains((await ReportAsync(admin, "overview")).GetProperty("tiles").EnumerateArray(), t => t.GetProperty("kind").GetString() == "money");

        // The money on the overview is only for those who may read the books.
        Assert.DoesNotContain((await ReportAsync(doctor, "overview")).GetProperty("tiles").EnumerateArray(), t => t.GetProperty("kind").GetString() == "money");
        Assert.Contains((await ReportAsync(accountant, "overview")).GetProperty("tiles").EnumerateArray(), t => t.GetProperty("kind").GetString() == "money");
    }

    [Fact]
    public async Task Patients_report_counts_each_person_once_with_their_gender_and_age()
    {
        var reception = await api.FeeDeskAsync();
        var query = $"?from={KabulClock.Today.AddDays(-1):yyyy-MM-dd}&to={Today}";
        var before = await ReportAsync(reception, "patients", query);

        await NewVisitAsync(Gender.Male, 70);
        await NewVisitAsync(Gender.Female, 10);
        var (_, cancelled) = await NewVisitAsync(Gender.Female, 45);
        Assert.Equal(HttpStatusCode.OK, (await reception.PostAsJsonAsync($"/api/visits/{cancelled.Id}/status", new ChangeVisitStatusRequest(VisitStatus.Cancelled, cancelled.Version))).StatusCode);

        var after = await ReportAsync(reception, "patients", query);
        Assert.Equal(2, Tile(after, "مراجعات") - Tile(before, "مراجعات"));
        Assert.Equal(2, Tile(after, "تعداد مریضان") - Tile(before, "تعداد مریضان"));
        Assert.Equal(3, Tile(after, "مریضان جدید") - Tile(before, "مریضان جدید"));
        Assert.Equal(1, Tile(after, "مراجعات لغو شده") - Tile(before, "مراجعات لغو شده"));

        static int Count(JsonElement report, string table, string row) =>
            Rows(Table(report, table)).Single(r => r[0].GetString() == row)[1].GetInt32();
        Assert.Equal(1, Count(after, "مریضان به تفکیک جنسیت", "مرد") - Count(before, "مریضان به تفکیک جنسیت", "مرد"));
        Assert.Equal(1, Count(after, "مریضان به تفکیک جنسیت", "زن") - Count(before, "مریضان به تفکیک جنسیت", "زن"));
        Assert.Equal(1, Count(after, "مریضان به تفکیک سن", "۶۰ سال و بالاتر") - Count(before, "مریضان به تفکیک سن", "۶۰ سال و بالاتر"));
        Assert.Equal(1, Count(after, "مریضان به تفکیک سن", "کمتر از ۱۵ سال") - Count(before, "مریضان به تفکیک سن", "کمتر از ۱۵ سال"));

        Assert.Equal(2, after.GetProperty("trend").GetProperty("points").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, (await ReportAsync(reception, "patients")).GetProperty("trend").ValueKind);
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
        Assert.DoesNotContain(patient.FileNumber, report.GetRawText());
    }

    [Fact]
    public async Task Income_lines_add_up_to_the_books_and_the_result_takes_out_every_cost()
    {
        var accountant = await UserAsync(Roles.Accountant);
        await NewVisitAsync();
        await Read<JsonElement>(await accountant.SendAsync(ApiFixture.Keyed("/api/expenses", new CreateExpenseRequest("برق", null, 25, KabulClock.Today))), HttpStatusCode.Created);

        var report = await ReportAsync(accountant, "income");
        var account = Rows(Table(report, "حساب دوره")).ToDictionary(r => r[0].GetString()!, r => r[1].GetDecimal());
        var gross = account["درآمد خدمات"] + account["فروش دواخانه"] + account["فروش عینک"];

        Assert.Equal(gross, Rows(Table(report, "درآمد به تفکیک خدمت")).Sum(r => r[1].GetDecimal()));
        Assert.Equal(account["درآمد خالص"], Tile(report, "درآمد خالص"));
        Assert.Equal(account["درآمد خالص"] + account["کم: قیمت خرید جنس فروخته‌شده"] + account["کم: مصارف"] + account["کم: تعدیل موجودی"], Tile(report, "نتیجه دوره"));
        Assert.True(Tile(report, "مصارف") >= 25);
    }

    [Fact]
    public async Task Outstanding_and_adjustments_follow_the_money()
    {
        var accountant = await UserAsync(Roles.Accountant);
        var cashier = await api.FeeCashierAsync();
        var token = await api.NewTokenAsync();

        Assert.Contains(Rows(Table(await ReportAsync(accountant, "outstanding"), "فاکتورهای باز")), r => r[0].GetString() == token.InvoiceNumber);

        var reason = Name("دلیل");
        Assert.Equal(HttpStatusCode.NoContent, (await accountant.PostAsJsonAsync($"/api/invoices/{token.InvoiceId}/discount", new DiscountRequest(10, reason, token.InvoiceVersion))).StatusCode);
        var payment = await Read<PaymentRow>(await cashier.SendAsync(ApiFixture.Keyed("/api/payments", new PaymentRequest(token.InvoiceId, token.Balance - 10, PaymentMethod.Cash, null))), HttpStatusCode.Created);
        Assert.DoesNotContain(Rows(Table(await ReportAsync(accountant, "outstanding"), "فاکتورهای باز")), r => r[0].GetString() == token.InvoiceNumber);

        var refundReason = Name("برگشت");
        await Read<JsonElement>(await accountant.SendAsync(ApiFixture.Keyed("/api/refunds", new RefundRequest(payment.Id, 5, refundReason))), HttpStatusCode.Created);

        var adjustments = await ReportAsync(accountant, "adjustments");
        var discount = Rows(Table(adjustments, "تخفیف‌ها")).Single(r => r[3].GetString() == reason);
        Assert.Equal(token.InvoiceNumber, discount[0].GetString());
        Assert.Equal(10, discount[2].GetDecimal());
        Assert.Contains(Rows(Table(adjustments, "برگشت وجه")), r => r[4].GetString() == refundReason && r[3].GetDecimal() == 5);
    }

    [Fact]
    public async Task Cash_report_shows_each_cashier_and_any_shortage_at_closing()
    {
        var accountant = await UserAsync(Roles.Accountant);
        var name = Name("c");
        await api.CreateUserAsync(name, Roles.Cashier);
        var cashier = await api.LoginAsync(name);
        var register = await InDbAsync(async db =>
        {
            var row = new CashRegister { Name = name };
            db.CashRegisters.Add(row);
            await db.SaveChangesAsync();
            return row.Id;
        });
        var opened = await Read<JsonElement>(await cashier.PostAsJsonAsync("/api/cash/sessions", new OpenSessionRequest(register, 10)), HttpStatusCode.Created);
        var token = await api.NewTokenAsync();
        await Read<PaymentRow>(await cashier.SendAsync(ApiFixture.Keyed("/api/payments", new PaymentRequest(token.InvoiceId, token.Balance, PaymentMethod.Cash, null, token.InvoiceVersion))), HttpStatusCode.Created);

        var open = Rows(Table(await ReportAsync(accountant, "cash"), "باز و بسته شدن صندوق‌ها")).Single(r => r[0].GetString() == name);
        Assert.Equal(JsonValueKind.Null, open[3].ValueKind);
        Assert.Equal(JsonValueKind.Null, open[8].ValueKind);

        var closed = await Read<JsonElement>(await cashier.PostAsJsonAsync($"/api/cash/sessions/{opened.GetProperty("id").GetInt64()}/close", new CloseSessionRequest(token.Balance + 7, 0)));
        Assert.NotEqual(JsonValueKind.Null, closed.GetProperty("closedAt").ValueKind);

        var row = Rows(Table(await ReportAsync(accountant, "cash"), "باز و بسته شدن صندوق‌ها")).Single(r => r[0].GetString() == name);
        Assert.Equal(token.Balance, row[5].GetDecimal());
        Assert.Equal(token.Balance + 10, row[6].GetDecimal());
        Assert.Equal(-3, row[8].GetDecimal());
    }

    [Fact]
    public async Task Stock_report_warns_about_low_stock_and_expiry()
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
        Assert.Contains(Rows(Table(report, "اجناس رو به ختم")), r => r[0].GetString() == low && r[1].GetDecimal() == 4);
        var expiring = Rows(Table(report, "تاریخ‌گذشته و نزدیک به ختم تاریخ")).Select(r => r[0].GetString()).ToList();
        Assert.Contains(expired, expiring);
        Assert.DoesNotContain(fresh, expiring);
        Assert.True(Tile(report, "بسته‌های تاریخ‌گذشته") >= 1);
    }

    [Fact]
    public async Task A_department_sees_only_its_own_sales()
    {
        var optical = await UserAsync(Roles.Optical);
        var pharmacist = await UserAsync(Roles.Pharmacist);

        var opticalReport = await ReportAsync(optical, "sales");
        Assert.DoesNotContain(opticalReport.GetProperty("tiles").EnumerateArray(), t => t.GetProperty("label").GetString() == "فروش دواخانه");
        Assert.DoesNotContain(opticalReport.GetProperty("tables").EnumerateArray(), t => t.GetProperty("title").GetString() == "نسخه‌های دوا");

        var pharmacyReport = await ReportAsync(pharmacist, "sales");
        Assert.DoesNotContain(pharmacyReport.GetProperty("tiles").EnumerateArray(), t => t.GetProperty("label").GetString() == "فروش عینک");
        Assert.Contains(pharmacyReport.GetProperty("tables").EnumerateArray(), t => t.GetProperty("title").GetString() == "نسخه‌های دوا");
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
