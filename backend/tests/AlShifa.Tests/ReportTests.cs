using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Clinical;
using AlShifa.Api.Features.Finance;
using AlShifa.Api.Features.Inventory;
using AlShifa.Api.Features.Patients;
using AlShifa.Api.Features.Reports;
using AlShifa.Api.Features.Settings;
using AlShifa.Api.Features.Users;
using AlShifa.Api.Features.Visits;
using AlShifa.Api.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace AlShifa.Tests;

[Collection("api")]
public sealed class ReportTests(ApiFixture api)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private static string Name(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..20];

    private async Task<HttpClient> UserAsync(params string[] roles)
    {
        var name = Name("u");
        await api.CreateUserAsync(name, roles);
        return await api.LoginAsync(name);
    }

    private static HttpRequestMessage Post(string url, object body)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        message.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return message;
    }

    private static async Task<T> Created<T>(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, text);
        return JsonSerializer.Deserialize<T>(text, JsonOptions)!;
    }

    private async Task<(HttpClient Cashier, InvoiceRow Invoice)> ChargedInvoiceAsync(decimal price)
    {
        var admin = await UserAsync(Roles.SuperAdmin);
        var service = await admin.PostAsJsonAsync("/api/services", new SaveServiceRequest(Name("S"), "خدمت " + Name("n"), null, true, price));
        var serviceId = JsonDocument.Parse(await service.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt32();

        var register = await admin.PostAsJsonAsync("/api/cash/registers", new SaveRegisterRequest(Name("صندوق")));
        var registerId = JsonDocument.Parse(await register.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt32();

        var cashier = await UserAsync(Roles.Cashier);
        await cashier.PostAsJsonAsync("/api/cash/sessions", new OpenSessionRequest(registerId, 0));
        var invoice = await Created<InvoiceRow>(await cashier.SendAsync(Post("/api/invoices", new CreateInvoiceRequest(null, null, [new InvoiceLineInput(serviceId, 1)]))));
        return (cashier, invoice);
    }

    [Fact]
    public async Task Financial_report_moves_by_exactly_the_recorded_transactions()
    {
        var accountant = await UserAsync(Roles.Accountant);
        var before = (await accountant.GetFromJsonAsync<FinancialReport>("/api/reports/financial", JsonOptions))!;

        var (cashier, invoice) = await ChargedInvoiceAsync(1000);
        var payment = await Created<PaymentRow>(await cashier.SendAsync(Post("/api/payments", new PaymentRequest(invoice.Id, 600, PaymentMethod.Cash, null))));
        await Created<object>(await accountant.SendAsync(Post("/api/refunds", new RefundRequest(payment.Id, 100, "برگشت"))));
        await Created<object>(await accountant.SendAsync(Post("/api/expenses", new CreateExpenseRequest("برق", null, 50, DateOnly.FromDateTime(DateTime.UtcNow)))));

        var after = (await accountant.GetFromJsonAsync<FinancialReport>("/api/reports/financial", JsonOptions))!;

        Assert.Equal(1000, after.ServiceRevenue - before.ServiceRevenue);
        Assert.Equal(100, after.Refunds - before.Refunds);
        Assert.Equal(900, after.NetRevenue - before.NetRevenue);
        Assert.Equal(600, after.Receipts - before.Receipts);
        Assert.Equal(50, after.Expenses - before.Expenses);
        Assert.Equal(400, after.OutstandingNow - before.OutstandingNow);
    }

    [Fact]
    public async Task Dashboard_counts_today_by_status_gender_and_department_and_excludes_cancelled_from_attendance()
    {
        var reception = await UserAsync(Roles.Receptionist);
        var departments = JsonDocument.Parse(await reception.GetStringAsync("/api/lookups/departments")).RootElement;
        var department = departments.EnumerateArray().First();
        var departmentId = department.GetProperty("id").GetInt32();
        var departmentName = department.GetProperty("name").GetString()!;

        var before = (await reception.GetFromJsonAsync<DashboardReport>("/api/reports/dashboard", JsonOptions))!;

        var female = await Created<PatientDto>(await reception.SendAsync(Post("/api/patients", new SavePatientRequest(Name("مریض"), null, null, Gender.Female, null, 30, null, null))));
        await Created<VisitRow>(await reception.SendAsync(Post("/api/visits", new CreateVisitRequest(female.Id, departmentId, null, null, null, await api.PaidTokenAsync()))));

        var male = await Created<PatientDto>(await reception.SendAsync(Post("/api/patients", new SavePatientRequest(Name("مریض"), null, null, Gender.Male, null, 30, null, null))));
        var cancelled = await Created<VisitRow>(await reception.SendAsync(Post("/api/visits", new CreateVisitRequest(male.Id, departmentId, null, null, null, await api.PaidTokenAsync()))));
        var change = await reception.PostAsJsonAsync($"/api/visits/{cancelled.Id}/status", new ChangeVisitStatusRequest(VisitStatus.Cancelled, cancelled.Version));
        Assert.Equal(HttpStatusCode.OK, change.StatusCode);

        var after = (await reception.GetFromJsonAsync<DashboardReport>("/api/reports/dashboard", JsonOptions))!;

        Assert.Equal(6, after.Days.Count);
        Assert.Equal(after.Today, after.Days[^1].Date);
        var (was, now) = (before.Days[^1], after.Days[^1]);
        Assert.Equal(1, now.Female - was.Female);
        Assert.Equal(0, now.Male - was.Male);
        Assert.Equal(1, now.Active - was.Active);
        Assert.Equal(1, now.Cancelled - was.Cancelled);
        Assert.Equal(0, now.Completed - was.Completed);
        Assert.Equal(1, after.VisitsByDepartment.Single(r => r.Name == departmentName).Count - before.VisitsByDepartment.Single(r => r.Name == departmentName).Count);
        Assert.Equal(1, Count(after, "Cancelled") - Count(before, "Cancelled"));

        static int Count(DashboardReport report, string status) => report.TodayByStatus.FirstOrDefault(r => r.Name == status)?.Count ?? 0;
    }

    [Fact]
    public async Task Dashboard_needs_the_reports_permission()
    {
        foreach (var role in new[] { Roles.Triage, Roles.AudioStation })
        {
            var client = await UserAsync(role);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/reports/dashboard")).StatusCode);
        }

        // The Super Admin holds every role's permissions, so the dashboard opens.
        var admin = await UserAsync(Roles.SuperAdmin);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/reports/dashboard")).StatusCode);
    }

    [Fact]
    public async Task Clinical_staff_cannot_read_financial_reports_but_see_operations()
    {
        var doctor = await UserAsync(Roles.Doctor);
        Assert.Equal(HttpStatusCode.Forbidden, (await doctor.GetAsync("/api/reports/financial")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await doctor.GetAsync("/api/reports/overview")).StatusCode);

        var manager = await UserAsync(Roles.Manager);
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync("/api/reports/financial")).StatusCode);

        var bad = await manager.GetAsync("/api/reports/overview?from=2026-02-01&to=2026-01-01");
        Assert.Contains("invalid_range", await bad.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Printed_receipt_has_hospital_details_a_unique_number_and_no_clinical_data()
    {
        var admin = await UserAsync(Roles.SuperAdmin);
        var hospital = Name("شفاخانه");
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/settings", new SaveSettingsRequest(hospital, "کابل", "0700000000", "پاورقی"))).StatusCode);

        var (cashier, invoice) = await ChargedInvoiceAsync(300);
        var first = await Created<PaymentRow>(await cashier.SendAsync(Post("/api/payments", new PaymentRequest(invoice.Id, 100, PaymentMethod.Cash, null))));
        var second = await Created<PaymentRow>(await cashier.SendAsync(Post("/api/payments", new PaymentRequest(invoice.Id, 50, PaymentMethod.Cash, null))));
        Assert.NotEqual(first.ReceiptNumber, second.ReceiptNumber);

        var doc = (await cashier.GetFromJsonAsync<PrintDocument>($"/api/print/receipt/{first.Id}", JsonOptions))!;
        Assert.Equal(first.ReceiptNumber, doc.Number);
        Assert.Equal(hospital, doc.Hospital.HospitalName);
        Assert.Equal(("مبلغ", "100 افغانی"), (doc.Totals![0].Label, doc.Totals[0].Value));

        // 150 of the 300 is still owed after both payments, so the receipt names the balance rather than reading as settled.
        Assert.Contains(doc.Fields, f => f.Label == "مانده فاکتور" && f.Value == "150 افغانی");
    }

    [Fact]
    public async Task Each_printout_is_limited_to_its_own_permission()
    {
        var (cashier, invoice) = await ChargedInvoiceAsync(300);
        var doctor = await UserAsync(Roles.Doctor);
        Assert.Equal(HttpStatusCode.Forbidden, (await doctor.GetAsync($"/api/print/invoice/{invoice.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await cashier.GetAsync($"/api/print/invoice/{invoice.Id}")).StatusCode);

        var reception = await UserAsync(Roles.Receptionist);
        var patient = await Created<PatientDto>(await reception.SendAsync(Post("/api/patients", new SavePatientRequest(Name("مریض"), null, null, Gender.Female, null, 30, null, null))));
        Assert.Equal(HttpStatusCode.OK, (await reception.GetAsync($"/api/print/patient/{patient.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync($"/api/print/patient/{patient.Id}")).StatusCode);
    }

    private async Task<(DiagnosticOrderDto Order, HttpClient Lab)> LabOrderAsync()
    {
        var reception = await UserAsync(Roles.Receptionist);
        var patient = await Created<PatientDto>(await reception.SendAsync(Post("/api/patients", new SavePatientRequest(Name("مریض"), null, null, Gender.Male, null, 30, null, null))));
        var admin = await UserAsync(Roles.SuperAdmin);
        var test = await admin.PostAsJsonAsync("/api/diagnostic-tests", new SaveTestRequest(Name("T"), true));
        var testId = JsonDocument.Parse(await test.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt32();
        var lab = await UserAsync(Roles.Lab);
        var order = await Created<DiagnosticOrderDto>(await lab.SendAsync(Post("/api/diagnostic-orders", new CreateDiagnosticOrderRequest(patient.Id, null, testId, null))));
        return (order, lab);
    }

    private static MultipartFormDataContent Upload(string ownerType, long ownerId, byte[] bytes, string fileName)
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return new MultipartFormDataContent
        {
            { new StringContent(ownerType), "ownerType" },
            { new StringContent(ownerId.ToString()), "ownerId" },
            { file, "file", fileName },
        };
    }

    private static readonly byte[] TinyPdf = "%PDF-1.4 test"u8.ToArray();

    [Fact]
    public async Task Lab_attachment_is_stored_and_only_permitted_users_can_download_it()
    {
        var (order, lab) = await LabOrderAsync();

        var uploaded = await lab.PostAsync("/api/attachments", Upload("DiagnosticOrder", order.Id, TinyPdf, "result.pdf"));
        var row = await Created<AttachmentRow>(uploaded);
        Assert.Equal("application/pdf", row.ContentType);

        var download = await lab.GetAsync($"/api/attachments/{row.Id}");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(TinyPdf, await download.Content.ReadAsByteArrayAsync());
        Assert.Equal("nosniff", download.Headers.GetValues("X-Content-Type-Options").Single());

        var cashier = await UserAsync(Roles.Cashier);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync($"/api/attachments/{row.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.NewClient().GetAsync($"/api/attachments/{row.Id}")).StatusCode);
    }

    [Fact]
    public async Task Disguised_or_oversized_files_are_refused()
    {
        var (order, lab) = await LabOrderAsync();

        // An HTML page renamed to .pdf is judged by its content, not its name.
        var disguised = await lab.PostAsync("/api/attachments", Upload("DiagnosticOrder", order.Id, "<script>alert(1)</script>"u8.ToArray(), "result.pdf"));
        Assert.Contains("file_type_not_allowed", await disguised.Content.ReadAsStringAsync());

        var large = new byte[(10 * 1024 * 1024) + 1];
        TinyPdf.CopyTo(large, 0);
        var oversized = await lab.PostAsync("/api/attachments", Upload("DiagnosticOrder", order.Id, large, "big.pdf"));
        Assert.Contains("file_too_large", await oversized.Content.ReadAsStringAsync());

        var unknownOwner = await lab.PostAsync("/api/attachments", Upload("Patient", order.PatientId, TinyPdf, "x.pdf"));
        Assert.Contains("invalid_owner", await unknownOwner.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Path_in_the_uploaded_name_cannot_escape_storage()
    {
        var (order, lab) = await LabOrderAsync();
        var row = await Created<AttachmentRow>(await lab.PostAsync("/api/attachments", Upload("DiagnosticOrder", order.Id, TinyPdf, "..\\..\\evil.pdf")));
        Assert.DoesNotContain("..", row.FileName);
    }
}
