using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Clinical;
using AlShifa.Api.Features.Finance;
using AlShifa.Api.Features.Patients;
using AlShifa.Api.Features.Users;
using AlShifa.Api.Features.Visits;
using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AlShifa.Tests;

/// <summary>Doctor queues, simultaneous steps of one visit, tariffs and the database safety rules.</summary>
[Collection("api")]
public sealed class FinalReadinessTests(ApiFixture api)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static string Name(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..20];

    private async Task<(HttpClient Client, long UserId)> UserAsync(params string[] roles)
    {
        var name = Name("u");
        var id = await api.CreateUserAsync(name, roles);
        return (await api.LoginAsync(name), id);
    }

    private static async Task<T> Read<T>(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, text);
        return JsonSerializer.Deserialize<T>(text, JsonOptions)!;
    }

    private async Task<T> InDbAsync<T>(Func<AppDbContext, Task<T>> read)
    {
        using var scope = api.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private async Task<long> DoctorStaffAsync(long? userId) => await InDbAsync(async db =>
    {
        var staff = new Staff { FullName = Name("داکتر"), IsDoctor = true, IsActive = true, UserId = userId };
        db.Staff.Add(staff);
        await db.SaveChangesAsync();
        return staff.Id;
    });

    private async Task<PatientDto> NewPatientAsync()
    {
        var desk = await api.FeeDeskAsync();
        return await Read<PatientDto>(await desk.SendAsync(ApiFixture.Keyed("/api/patients",
            new SavePatientRequest(Name("احمد"), null, null, Gender.Male, null, 30, null, null))), HttpStatusCode.Created);
    }

    private async Task<(PatientDto Patient, VisitRow Visit)> VisitAsync(long? doctorStaffId, string department = "doctor")
    {
        var desk = await api.FeeDeskAsync();
        var patient = await NewPatientAsync();
        var token = await api.PaidTokenAsync(department);
        var visit = await Read<VisitRow>(await desk.SendAsync(ApiFixture.Keyed("/api/visits",
            new CreateVisitRequest(patient.Id, await api.DepartmentIdAsync(department), doctorStaffId, null, null, token))), HttpStatusCode.Created);
        if (department == "doctor")
        {
            await api.CompleteVisionAsync(visit.Id);
            visit = (await desk.GetFromJsonAsync<VisitRow>($"/api/visits/{visit.Id}", JsonOptions))!;
        }

        return (patient, visit);
    }

    private static async Task<List<VisitRow>> QueueAsync(HttpClient doctor) =>
        (await doctor.GetFromJsonAsync<List<VisitRow>>("/api/clinical/worklist?kind=doctor", JsonOptions))!;

    private static SaveConsultationRequest Exam(int? version = null) =>
        new("یافته", null, null, null, [new DiagnosisInput(EyeSide.OD, "تشخیص")], version);

    [Fact]
    public async Task Each_doctor_sees_only_their_own_assigned_patients()
    {
        var (first, firstUser) = await UserAsync(Roles.Doctor);
        var (second, secondUser) = await UserAsync(Roles.Doctor);
        var (_, firstVisit) = await VisitAsync(await DoctorStaffAsync(firstUser));
        var (_, secondVisit) = await VisitAsync(await DoctorStaffAsync(secondUser));

        var firstQueue = await QueueAsync(first);
        Assert.Contains(firstQueue, v => v.Id == firstVisit.Id);
        Assert.DoesNotContain(firstQueue, v => v.Id == secondVisit.Id);

        var secondQueue = await QueueAsync(second);
        Assert.Contains(secondQueue, v => v.Id == secondVisit.Id);
        Assert.DoesNotContain(secondQueue, v => v.Id == firstVisit.Id);
    }

    [Fact]
    public async Task A_patient_assigned_to_a_doctor_without_a_login_is_not_lost()
    {
        var (doctor, _) = await UserAsync(Roles.Doctor);
        var (_, visit) = await VisitAsync(await DoctorStaffAsync(null));

        Assert.Contains(await QueueAsync(doctor), v => v.Id == visit.Id);
    }

    [Fact]
    public async Task The_doctor_who_takes_an_unassigned_patient_gets_them_back_after_refraction()
    {
        var (taker, takerUser) = await UserAsync(Roles.Doctor);
        var (other, _) = await UserAsync(Roles.Doctor);
        var takerStaff = await DoctorStaffAsync(takerUser);
        var (triage, _) = await UserAsync(Roles.Triage);

        // Starts at Vision, so the patient belongs to no department of doctors.
        var (patient, visit) = await VisitAsync(null, "triage");
        await Read<PreExamDto>(await triage.PutAsJsonAsync($"/api/pre-examinations/{visit.Id}", new SavePreExamRequest(null, null, "6/9", null, null, null, null, null, null)));
        Assert.Equal(HttpStatusCode.NoContent, (await triage.PostAsync($"/api/pre-examinations/{visit.Id}/complete", null)).StatusCode);
        Assert.Contains(await QueueAsync(other), v => v.Id == visit.Id);

        var consult = await Read<ConsultationDto>(await taker.PutAsJsonAsync($"/api/consultations/{visit.Id}", Exam()));
        var claimed = await taker.GetFromJsonAsync<VisitRow>($"/api/visits/{visit.Id}", JsonOptions);
        Assert.Equal(takerStaff, claimed!.DoctorStaffId);
        Assert.Contains(await QueueAsync(taker), v => v.Id == visit.Id);
        Assert.DoesNotContain(await QueueAsync(other), v => v.Id == visit.Id);

        var tests = await taker.GetFromJsonAsync<JsonElement>("/api/diagnostic-tests");
        var refraction = tests.EnumerateArray().First(t => t.GetProperty("kind").GetString() == "Refraction").GetProperty("id").GetInt32();
        var order = await Read<DiagnosticOrderDto>(await taker.SendAsync(ApiFixture.Keyed("/api/diagnostic-orders", new CreateDiagnosticOrderRequest(patient.Id, visit.Id, refraction, null))), HttpStatusCode.Created);
        await api.PayFeeAsync(await api.OrderFeeAsync(patient.FileNumber, order.TestName));

        // Waiting at refraction it is still this doctor's patient, and it returns to this doctor alone.
        Assert.Contains(await QueueAsync(taker), v => v.Id == visit.Id && v.Status == nameof(VisitStatus.AwaitingDiagnostic));
        await Read<RefractionDto>(await triage.PutAsJsonAsync($"/api/refractions/{visit.Id}", new SaveRefractionRequest("6/18", null, null, null, null, null, null, null, null, [], null)));
        Assert.Equal(HttpStatusCode.NoContent, (await triage.PostAsync($"/api/refractions/{visit.Id}/complete", null)).StatusCode);

        Assert.Contains(await QueueAsync(taker), v => v.Id == visit.Id && v.Status == nameof(VisitStatus.ReadyForDoctor));
        Assert.DoesNotContain(await QueueAsync(other), v => v.Id == visit.Id);
        Assert.Equal(consult.Id, (await taker.GetFromJsonAsync<ChartDto>($"/api/consultations/{visit.Id}/chart", JsonOptions))!.Consultation!.Id);
    }

    [Fact]
    public async Task Two_doctors_starting_the_same_exam_together_have_one_author_and_one_clear_refusal()
    {
        var (first, _) = await UserAsync(Roles.Doctor);
        var (second, _) = await UserAsync(Roles.Doctor);
        var (_, visit) = await VisitAsync(null);

        var responses = await Task.WhenAll(
            first.PutAsJsonAsync($"/api/consultations/{visit.Id}", Exam()),
            second.PutAsJsonAsync($"/api/consultations/{visit.Id}", Exam()));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        var refused = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Contains("concurrency_conflict", await refused.Content.ReadAsStringAsync());
        Assert.Equal(1, await InDbAsync(db => db.Consultations.CountAsync(c => c.VisitId == visit.Id)));
    }

    [Fact]
    public async Task Simultaneous_saves_of_one_exam_are_told_the_record_changed_and_never_overwrite()
    {
        var (doctor, _) = await UserAsync(Roles.Doctor);
        var (_, visit) = await VisitAsync(null);
        var created = await Read<ConsultationDto>(await doctor.PutAsJsonAsync($"/api/consultations/{visit.Id}", Exam()));

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            doctor.PutAsJsonAsync($"/api/consultations/{visit.Id}", new SaveConsultationRequest($"یافته-{i}", null, null, null, [new DiagnosisInput(EyeSide.OD, $"تشخیص-{i}")], created.Version))));

        var winner = Assert.Single(Enumerable.Range(0, 6), i => responses[i].StatusCode == HttpStatusCode.OK);
        foreach (var lost in responses.Where(r => r.StatusCode != HttpStatusCode.OK))
        {
            Assert.Equal(HttpStatusCode.Conflict, lost.StatusCode);
            Assert.Contains("concurrency_conflict", await lost.Content.ReadAsStringAsync());
        }

        var chart = await doctor.GetFromJsonAsync<ChartDto>($"/api/consultations/{visit.Id}/chart", JsonOptions);
        Assert.Equal($"یافته-{winner}", chart!.Consultation!.FindingsOd);
        Assert.Equal($"تشخیص-{winner}", Assert.Single(chart.Consultation.Diagnoses).Text);
    }

    [Fact]
    public async Task Two_requests_finishing_together_still_return_the_patient_to_the_doctor()
    {
        var (doctor, _) = await UserAsync(Roles.Doctor);
        var (lab, _) = await UserAsync(Roles.Lab);
        var admin = (await UserAsync(Roles.SuperAdmin)).Client;
        var (patient, visit) = await VisitAsync(null);
        await Read<ConsultationDto>(await doctor.PutAsJsonAsync($"/api/consultations/{visit.Id}", Exam()));

        var serviceId = await InDbAsync(db => db.Services.Where(s => s.Code == ServiceCodes.Imaging).Select(s => s.Id).SingleAsync());
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/diagnostic-tests", new SaveTestRequest(Name("عکس"), true, serviceId, DiagnosticKind.Imaging))).StatusCode);
        var tests = await doctor.GetFromJsonAsync<JsonElement>("/api/diagnostic-tests");
        var imaging = tests.EnumerateArray().Where(t => t.GetProperty("kind").GetString() == "Imaging").Select(t => t.GetProperty("id").GetInt32()).Take(2).ToList();

        var orders = new List<DiagnosticOrderDto>();
        foreach (var testId in imaging)
        {
            var order = await Read<DiagnosticOrderDto>(await doctor.SendAsync(ApiFixture.Keyed("/api/diagnostic-orders", new CreateDiagnosticOrderRequest(patient.Id, visit.Id, testId, null))), HttpStatusCode.Created);
            await api.PayFeeAsync(await api.OrderFeeAsync(patient.FileNumber, order.TestName));
            orders.Add(order);
        }

        var results = await Task.WhenAll(orders.Select(o => lab.PostAsJsonAsync($"/api/diagnostic-orders/{o.Id}/result", new SaveResultRequest("نتیجه", o.Version))));
        Assert.All(results, r => Assert.Equal(HttpStatusCode.NoContent, r.StatusCode));

        Assert.Equal(nameof(VisitStatus.ReadyForDoctor), (await doctor.GetFromJsonAsync<VisitRow>($"/api/visits/{visit.Id}", JsonOptions))!.Status);
    }

    [Fact]
    public async Task One_patient_at_two_desks_opens_one_visit_and_keeps_the_other_token_unused()
    {
        var desk = await api.FeeDeskAsync();
        var patient = await NewPatientAsync();
        var department = await api.DepartmentIdAsync("triage");
        var doctorDepartment = await api.DepartmentIdAsync("doctor");
        var tokens = await Task.WhenAll(api.PaidTokenAsync(), api.PaidTokenAsync("doctor"));

        // Different departments, so only the open-visit rule can stop the second one.
        var responses = await Task.WhenAll(
            desk.SendAsync(ApiFixture.Keyed("/api/visits", new CreateVisitRequest(patient.Id, department, null, null, null, tokens[0]))),
            desk.SendAsync(ApiFixture.Keyed("/api/visits", new CreateVisitRequest(patient.Id, doctorDepartment, null, null, null, tokens[1]))));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        var refused = Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Contains("duplicate_visit", await refused.Content.ReadAsStringAsync());
        Assert.Equal(1, await InDbAsync(db => db.Visits.CountAsync(v => v.PatientId == patient.Id)));
    }

    [Fact]
    public async Task Cancelling_a_visit_withdraws_its_unpaid_request_and_stops_at_a_paid_one()
    {
        var (doctor, _) = await UserAsync(Roles.Doctor);
        var desk = await api.FeeDeskAsync();
        var (patient, visit) = await VisitAsync(null);
        await Read<ConsultationDto>(await doctor.PutAsJsonAsync($"/api/consultations/{visit.Id}", Exam()));

        var tests = await doctor.GetFromJsonAsync<JsonElement>("/api/diagnostic-tests");
        var refraction = tests.EnumerateArray().First(t => t.GetProperty("kind").GetString() == "Refraction").GetProperty("id").GetInt32();
        var order = await Read<DiagnosticOrderDto>(await doctor.SendAsync(ApiFixture.Keyed("/api/diagnostic-orders", new CreateDiagnosticOrderRequest(patient.Id, visit.Id, refraction, null))), HttpStatusCode.Created);

        var current = await desk.GetFromJsonAsync<VisitRow>($"/api/visits/{visit.Id}", JsonOptions);
        Assert.Equal(HttpStatusCode.OK, (await desk.PostAsJsonAsync($"/api/visits/{visit.Id}/status", new ChangeVisitStatusRequest(VisitStatus.Cancelled, current!.Version))).StatusCode);

        var orderInvoice = await InDbAsync(db => db.DiagnosticOrders.Where(o => o.Id == order.Id).Select(o => o.Invoice!.Status).SingleAsync());
        Assert.Equal(InvoiceStatus.Void, orderInvoice);
        Assert.Equal(DiagnosticStatus.Cancelled, await InDbAsync(db => db.DiagnosticOrders.Where(o => o.Id == order.Id).Select(o => o.Status).SingleAsync()));

        // A cancelled visit takes no more clinical records.
        var (triage, _) = await UserAsync(Roles.Triage);
        var refused = await triage.PutAsJsonAsync($"/api/pre-examinations/{visit.Id}", new SavePreExamRequest(null, null, "6/9", null, null, null, null, null, null));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("visit_closed", await refused.Content.ReadAsStringAsync());

        var (paidPatient, paidVisit) = await VisitAsync(null);
        await Read<ConsultationDto>(await doctor.PutAsJsonAsync($"/api/consultations/{paidVisit.Id}", Exam()));
        var paidOrder = await Read<DiagnosticOrderDto>(await doctor.SendAsync(ApiFixture.Keyed("/api/diagnostic-orders", new CreateDiagnosticOrderRequest(paidPatient.Id, paidVisit.Id, refraction, null))), HttpStatusCode.Created);
        await api.PayFeeAsync(await api.OrderFeeAsync(paidPatient.FileNumber, paidOrder.TestName));
        var paidCurrent = await desk.GetFromJsonAsync<VisitRow>($"/api/visits/{paidVisit.Id}", JsonOptions);
        var stopped = await desk.PostAsJsonAsync($"/api/visits/{paidVisit.Id}/status", new ChangeVisitStatusRequest(VisitStatus.Cancelled, paidCurrent!.Version));
        Assert.Equal(HttpStatusCode.Conflict, stopped.StatusCode);
        Assert.Contains("line_already_paid", await stopped.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_refunded_token_leaves_the_list_and_cannot_open_a_visit()
    {
        var desk = await api.FeeDeskAsync();
        var (accountant, _) = await UserAsync(Roles.Accountant);
        var token = await api.NewTokenAsync();
        var cashier = await api.FeeCashierAsync();
        var paid = await Read<JsonElement>(await cashier.SendAsync(ApiFixture.Keyed("/api/payments", new PaymentRequest(token.InvoiceId, token.Balance, PaymentMethod.Cash, null, token.InvoiceVersion))), HttpStatusCode.Created);
        await Read<JsonElement>(await accountant.SendAsync(ApiFixture.Keyed("/api/refunds", new RefundRequest(paid.GetProperty("id").GetInt64(), token.Balance, "مریض رفت"))), HttpStatusCode.Created);

        var list = await desk.GetFromJsonAsync<List<VisitTokenRow>>("/api/visit-tokens", JsonOptions);
        Assert.DoesNotContain(list!, t => t.Id == token.Id);

        var patient = await NewPatientAsync();
        var refused = await desk.SendAsync(ApiFixture.Keyed("/api/visits", new CreateVisitRequest(patient.Id, token.DepartmentId, null, null, null, token.Id)));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("visit_fee_required", await refused.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_fully_discounted_invoice_leaves_the_unpaid_list()
    {
        var (admin, _) = await UserAsync(Roles.SuperAdmin);
        var (cashier, _) = await UserAsync(Roles.Cashier);
        var (accountant, _) = await UserAsync(Roles.Accountant);
        var service = await admin.PostAsJsonAsync("/api/services", new SaveServiceRequest(Name("S"), Name("خدمت"), null, true, 30));
        var serviceId = JsonDocument.Parse(await service.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt32();
        var invoice = await Read<InvoiceRow>(await cashier.SendAsync(ApiFixture.Keyed("/api/invoices", new CreateInvoiceRequest(null, null, [new InvoiceLineInput(serviceId, 1)]))), HttpStatusCode.Created);

        Assert.Equal(HttpStatusCode.NoContent, (await accountant.PostAsJsonAsync($"/api/invoices/{invoice.Id}/discount", new DiscountRequest(30, "تخفیف کامل", invoice.Version))).StatusCode);

        var unpaid = await cashier.GetFromJsonAsync<PagedResult<InvoiceRow>>($"/api/invoices?unpaid=true&q={invoice.InvoiceNumber}", JsonOptions);
        Assert.Empty(unpaid!.Items);
    }

    [Fact]
    public async Task Tariffs_are_set_by_finance_or_the_administrator_and_paid_flow_fees_cannot_be_zero()
    {
        var (accountant, _) = await UserAsync(Roles.Accountant);
        var (admin, _) = await UserAsync(Roles.SuperAdmin);
        var (reception, _) = await UserAsync(Roles.Receptionist);
        var (cashier, _) = await UserAsync(Roles.Cashier);

        var visitFee = await InDbAsync(db => db.Services.Where(s => s.Code == ServiceCodes.VisitFee).Select(s => s.Id).SingleAsync());
        var url = $"/api/services/{visitFee}/price";

        Assert.Equal(HttpStatusCode.Forbidden, (await reception.PostAsJsonAsync(url, new SetPriceRequest(120))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsJsonAsync(url, new SetPriceRequest(120))).StatusCode);

        var zero = await accountant.PostAsJsonAsync(url, new SetPriceRequest(0));
        Assert.Equal(HttpStatusCode.BadRequest, zero.StatusCode);
        Assert.Contains("price_required", await zero.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await accountant.PostAsJsonAsync(url, new SetPriceRequest(10.005m))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/diagnostic-tests", new SaveTestRequest(Name("t"), true, null, DiagnosticKind.Imaging))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/diagnostic-tests", new SaveTestRequest(Name("t"), true, 999999, DiagnosticKind.Imaging))).StatusCode);

        // The test suite's own fee is restored, and the change names both prices.
        Assert.Equal(HttpStatusCode.NoContent, (await accountant.PostAsJsonAsync(url, new SetPriceRequest(100))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync(url, new SetPriceRequest(100))).StatusCode);
        var key = visitFee.ToString();
        var details = await InDbAsync(db => db.AuditLogs.Where(a => a.Action == "ServicePriceChanged" && a.EntityId == key).OrderByDescending(a => a.Id).Select(a => a.Details).FirstAsync());
        Assert.Contains("=> 100.00", details);
    }

    [Fact]
    public async Task An_amount_finer_than_the_books_keep_is_refused()
    {
        var cashier = await api.FeeCashierAsync();
        var token = await api.NewTokenAsync();
        var refused = await cashier.SendAsync(ApiFixture.Keyed("/api/payments", new PaymentRequest(token.InvoiceId, 99.999m, PaymentMethod.Cash, null, token.InvoiceVersion)));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("invalid_amount", await refused.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_staff_link_to_a_missing_user_fails_clearly()
    {
        var (admin, _) = await UserAsync(Roles.SuperAdmin);
        var response = await admin.PostAsJsonAsync("/api/staff", new SaveStaffRequest(Name("داکتر"), null, true, true, null, 99999999));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("unknown_user", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("alshifa_test", true)]
    [InlineData("alshifa_dev", true)]
    [InlineData("alshifa_load", true)]
    [InlineData("alshifa", false)]
    [InlineData("alshifa_test_copy", false)]
    [InlineData("", false)]
    public void Only_rebuildable_databases_may_be_migrated_without_being_asked(string database, bool expected) =>
        Assert.Equal(expected, DatabaseSafety.IsDisposable($"Server=127.0.0.1;Port=3307;Database={database};User=x;Password=y;"));

    [Fact]
    public void Code_and_database_disagreeing_in_either_direction_is_a_problem()
    {
        Assert.Null(DatabaseSafety.SchemaProblem(["a", "b"], ["a", "b"]));
        Assert.Contains("missing", DatabaseSafety.SchemaProblem(["a"], ["a", "b"]));
        Assert.Contains("newer", DatabaseSafety.SchemaProblem(["a", "b", "c"], ["a", "b"]));
    }

    [Fact]
    public async Task The_model_has_no_change_missing_from_the_migrations()
    {
        Assert.False(await InDbAsync(db => Task.FromResult(db.Database.HasPendingModelChanges())));
        Assert.Null(await InDbAsync(db => DatabaseSafety.SchemaProblemAsync(db)));
    }
}
