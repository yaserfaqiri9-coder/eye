using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Clinical;
using AlShifa.Api.Features.Patients;
using AlShifa.Api.Features.Users;
using AlShifa.Api.Features.Visits;
using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AlShifa.Tests;

[Collection("api")]
public sealed class ClinicalTests(ApiFixture api)
{
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

    private static async Task<T> ReadCreated<T>(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, text);
        return JsonSerializer.Deserialize<T>(text, JsonOptions)!;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>A patient with an open visit in the given department, as reception would create it.</summary>
    private async Task<(PatientDto Patient, VisitRow Visit)> NewVisitAsync(string department = "داکتر")
    {
        var reception = await UserAsync(Roles.Receptionist);
        var patient = await ReadCreated<PatientDto>(await reception.SendAsync(Post("/api/patients",
            new SavePatientRequest(Name("احمد"), null, "محمود", Gender.Male, null, 30, null, null))));
        var departments = await reception.GetFromJsonAsync<List<LookupItem>>("/api/lookups/departments");
        var visit = await ReadCreated<VisitRow>(await reception.SendAsync(Post("/api/visits",
            new CreateVisitRequest(patient.Id, (int)departments!.Single(d => d.Name == department).Id, null, null))));
        return (patient, visit);
    }

    private async Task<int> NewTestAsync()
    {
        var admin = await UserAsync(Roles.SuperAdmin);
        var response = await admin.PostAsJsonAsync("/api/diagnostic-tests", new SaveTestRequest(Name("OCT"), true));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt32();
    }

    [Fact]
    public async Task Pre_exam_reaches_the_doctor_and_moves_the_visit()
    {
        var (_, visit) = await NewVisitAsync("معاینه ابتدایی");
        var triage = await UserAsync(Roles.Triage);

        var worklist = await triage.GetFromJsonAsync<List<VisitRow>>("/api/clinical/worklist?kind=triage");
        Assert.Contains(worklist!, v => v.Id == visit.Id);

        var saved = await triage.PutAsJsonAsync($"/api/pre-examinations/{visit.Id}",
            new SavePreExamRequest("تاری دید", null, "6/9", "6/12", "15", "16", null, null, null));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await triage.PostAsync($"/api/pre-examinations/{visit.Id}/complete", null)).StatusCode);

        var doctor = await UserAsync(Roles.Doctor);
        var queue = await doctor.GetFromJsonAsync<List<VisitRow>>("/api/clinical/worklist?kind=doctor");
        Assert.Contains(queue!, v => v.Id == visit.Id && v.Status == nameof(VisitStatus.ReadyForDoctor));

        var chart = await doctor.GetFromJsonAsync<ChartDto>($"/api/consultations/{visit.Id}/chart", JsonOptions);
        Assert.Equal("6/9", chart!.PreExam!.VisualAcuityOd);
        Assert.Equal("16", chart.PreExam.IopOs);
    }

    [Fact]
    public async Task Medical_values_are_stored_exactly_as_entered()
    {
        var (_, visit) = await NewVisitAsync();
        var doctor = await UserAsync(Roles.Doctor);

        var rx = await ReadCreated<PrescriptionDto>(await doctor.SendAsync(Post("/api/prescriptions",
            new CreatePrescriptionRequest(visit.Id, null, [new RxItemInput("قطره نمونه", "۱ قطره", "چشمی", "۷ روز", "هر ۶ ساعت", 2)]))));

        var item = Assert.Single(rx.Items);
        Assert.Equal("۱ قطره", item.Dose);
        Assert.Equal("هر ۶ ساعت", item.Instructions);
        Assert.Equal(2, item.Quantity);
    }

    [Fact]
    public async Task Prescription_reaches_pharmacy_without_diagnosis_or_confidential_notes()
    {
        var (_, visit) = await NewVisitAsync();
        var doctor = await UserAsync(Roles.Doctor);
        const string secret = "یادداشت-محرمانه-داکتر";
        const string diagnosis = "تشخیص-خصوصی";

        Assert.Equal(HttpStatusCode.OK, (await doctor.PutAsJsonAsync($"/api/consultations/{visit.Id}",
            new SaveConsultationRequest(null, null, null, secret, [new DiagnosisInput(EyeSide.OD, diagnosis)], null))).StatusCode);

        var rx = await ReadCreated<PrescriptionDto>(await doctor.SendAsync(Post("/api/prescriptions",
            new CreatePrescriptionRequest(visit.Id, null, [new RxItemInput("دوا", null, null, null, null, 1)]))));

        var pharmacist = await UserAsync(Roles.Pharmacist);
        var body = await pharmacist.GetStringAsync("/api/prescriptions?status=Issued");
        Assert.Contains(rx.RxNumber, body);
        Assert.DoesNotContain(secret, body);
        Assert.DoesNotContain(diagnosis, body);

        Assert.Equal(HttpStatusCode.Forbidden, (await pharmacist.GetAsync($"/api/consultations/{visit.Id}/chart")).StatusCode);
    }

    [Fact]
    public async Task Optical_prescription_reaches_optical_department()
    {
        var (_, visit) = await NewVisitAsync();
        var doctor = await UserAsync(Roles.Doctor);
        var rx = await ReadCreated<OpticalRxDto>(await doctor.SendAsync(Post("/api/optical-prescriptions",
            new CreateOpticalRxRequest(visit.Id, "-1.25", "-0.50", "90", null, "-1.00", null, null, null, "62", null))));

        var optical = await UserAsync(Roles.Optical);
        var list = await optical.GetFromJsonAsync<List<OpticalRxDto>>("/api/optical-prescriptions");
        var seen = Assert.Single(list!, r => r.Id == rx.Id);
        Assert.Equal("-1.25", seen.SphereOd);
        Assert.Equal("62", seen.Pd);
    }

    [Fact]
    public async Task Lab_order_flows_from_doctor_to_lab_and_result_returns_to_chart()
    {
        var (patient, visit) = await NewVisitAsync();
        var testId = await NewTestAsync();
        var doctor = await UserAsync(Roles.Doctor);

        var order = await ReadCreated<DiagnosticOrderDto>(await doctor.SendAsync(Post("/api/diagnostic-orders",
            new CreateDiagnosticOrderRequest(patient.Id, visit.Id, testId, "بررسی شبکیه"))));
        Assert.Equal(nameof(DiagnosticStatus.Requested), order.Status);

        var lab = await UserAsync(Roles.Lab);
        var queue = await lab.GetFromJsonAsync<PagedResult<DiagnosticOrderDto>>("/api/diagnostic-orders?status=Requested");
        Assert.Contains(queue!.Items, o => o.Id == order.Id);
        Assert.Equal(HttpStatusCode.Forbidden, (await lab.GetAsync($"/api/consultations/{visit.Id}/chart")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await lab.PostAsJsonAsync($"/api/diagnostic-orders/{order.Id}/start", new ChangeOrderStatusRequest(order.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await lab.PostAsJsonAsync($"/api/diagnostic-orders/{order.Id}/result", new SaveResultRequest("نتیجه نمونه", order.Version + 1))).StatusCode);

        var chart = await doctor.GetFromJsonAsync<ChartDto>($"/api/consultations/{visit.Id}/chart", JsonOptions);
        var seen = Assert.Single(chart!.DiagnosticOrders);
        Assert.Equal("نتیجه نمونه", seen.ResultText);
        Assert.Equal(nameof(DiagnosticStatus.ResultReady), seen.Status);

        var again = await lab.PostAsJsonAsync($"/api/diagnostic-orders/{order.Id}/result", new SaveResultRequest("نتیجه دوم", seen.Version));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task Lab_can_register_a_standalone_order()
    {
        var (patient, _) = await NewVisitAsync();
        var testId = await NewTestAsync();
        var lab = await UserAsync(Roles.Lab);

        var order = await ReadCreated<DiagnosticOrderDto>(await lab.SendAsync(Post("/api/diagnostic-orders",
            new CreateDiagnosticOrderRequest(patient.Id, null, testId, null))));
        Assert.Null(order.VisitId);
    }

    [Fact]
    public async Task Order_cannot_attach_to_another_patients_visit()
    {
        var (_, visit) = await NewVisitAsync();
        var (other, _) = await NewVisitAsync();
        var testId = await NewTestAsync();
        var doctor = await UserAsync(Roles.Doctor);

        var response = await doctor.SendAsync(Post("/api/diagnostic-orders", new CreateDiagnosticOrderRequest(other.Id, visit.Id, testId, null)));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("visit_mismatch", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Surgery_flows_to_department_and_viewer_cannot_read_clinical_detail()
    {
        var (_, visit) = await NewVisitAsync();
        var doctor = await UserAsync(Roles.Doctor);
        var surgery = await ReadCreated<SurgeryRow>(await doctor.SendAsync(Post("/api/surgeries", new CreateSurgeryRequest(visit.Id, "نوع نمونه", EyeSide.OD))));

        var team = await UserAsync(Roles.Surgery);
        var list = await team.GetFromJsonAsync<PagedResult<SurgeryRow>>("/api/surgeries");
        Assert.Contains(list!.Items, c => c.Id == surgery.Id);

        var early = await team.PostAsJsonAsync($"/api/surgeries/{surgery.Id}/status", new ChangeSurgeryStatusRequest(SurgeryStatus.Scheduled, surgery.Version));
        Assert.Contains("schedule_required", await early.Content.ReadAsStringAsync());

        var saved = await team.PutAsJsonAsync($"/api/surgeries/{surgery.Id}", new SaveSurgeryRequest(DateTime.UtcNow.AddDays(3), "بررسی نمونه", true, null, null, surgery.Version));
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);

        var scheduled = await team.PostAsJsonAsync($"/api/surgeries/{surgery.Id}/status", new ChangeSurgeryStatusRequest(SurgeryStatus.Scheduled, surgery.Version + 1));
        Assert.Equal(HttpStatusCode.NoContent, scheduled.StatusCode);

        var manager = await UserAsync(Roles.Manager);
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync("/api/surgeries")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync($"/api/surgeries/{surgery.Id}")).StatusCode);
    }

    [Fact]
    public async Task Finalized_consultation_changes_only_through_a_recorded_amendment()
    {
        var (_, visit) = await NewVisitAsync();
        var doctor = await UserAsync(Roles.Doctor);

        var saved = await doctor.PutAsJsonAsync($"/api/consultations/{visit.Id}", new SaveConsultationRequest("یافته اول", null, "پلان اول", null, null, null));
        var dto = (await saved.Content.ReadFromJsonAsync<ConsultationDto>())!;

        var finalized = await doctor.PostAsJsonAsync($"/api/consultations/{visit.Id}/finalize", new FinalizeRequest(dto.Version, CloseVisit: true));
        Assert.True(finalized.IsSuccessStatusCode, await finalized.Content.ReadAsStringAsync());
        dto = (await finalized.Content.ReadFromJsonAsync<ConsultationDto>())!;

        var direct = await doctor.PutAsJsonAsync($"/api/consultations/{visit.Id}", new SaveConsultationRequest("تغییر مستقیم", null, null, null, null, dto.Version));
        Assert.Equal(HttpStatusCode.Conflict, direct.StatusCode);
        Assert.Contains("already_finalized", await direct.Content.ReadAsStringAsync());

        var amend = await doctor.PostAsJsonAsync($"/api/consultations/{visit.Id}/amend", new AmendRequest("TreatmentPlan", "پلان اصلاح‌شده", "اشتباه در ثبت", dto.Version));
        Assert.Equal(HttpStatusCode.NoContent, amend.StatusCode);

        var chart = await doctor.GetFromJsonAsync<ChartDto>($"/api/consultations/{visit.Id}/chart", JsonOptions);
        Assert.Equal("پلان اصلاح‌شده", chart!.Consultation!.TreatmentPlan);
        var amendment = Assert.Single(chart.Amendments);
        Assert.Equal("پلان اول", amendment.OldValue);
        Assert.Equal("اشتباه در ثبت", amendment.Reason);
        Assert.Equal(nameof(VisitStatus.Completed), chart.Visit.Status);
    }

    [Fact]
    public async Task Concurrent_consultation_edit_is_detected()
    {
        var (_, visit) = await NewVisitAsync();
        var doctorA = await UserAsync(Roles.Doctor);
        var doctorB = await UserAsync(Roles.Doctor);

        var created = await doctorA.PutAsJsonAsync($"/api/consultations/{visit.Id}", new SaveConsultationRequest("اول", null, null, null, null, null));
        var dto = (await created.Content.ReadFromJsonAsync<ConsultationDto>())!;

        var first = await doctorA.PutAsJsonAsync($"/api/consultations/{visit.Id}", new SaveConsultationRequest("تغییر داکتر الف", null, null, null, null, dto.Version));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await doctorB.PutAsJsonAsync($"/api/consultations/{visit.Id}", new SaveConsultationRequest("تغییر داکتر ب", null, null, null, null, dto.Version));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Closed_record_needs_a_care_relationship_or_an_audited_emergency_reason()
    {
        var (patient, visit) = await NewVisitAsync();
        var doctor = await UserAsync(Roles.Doctor);
        var saved = await doctor.PutAsJsonAsync($"/api/consultations/{visit.Id}", new SaveConsultationRequest("یافته", null, null, null, null, null));
        var dto = (await saved.Content.ReadFromJsonAsync<ConsultationDto>())!;
        await doctor.PostAsJsonAsync($"/api/consultations/{visit.Id}/finalize", new FinalizeRequest(dto.Version, CloseVisit: true));

        var unrelatedName = Name("other");
        var unrelatedId = await api.CreateUserAsync(unrelatedName, Roles.Doctor);
        var unrelated = await api.LoginAsync(unrelatedName);

        var denied = await unrelated.GetAsync($"/api/consultations/{visit.Id}/chart");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Contains("clinical_access_required", await denied.Content.ReadAsStringAsync());

        var noReason = await unrelated.PostAsJsonAsync($"/api/clinical/patients/{patient.Id}/emergency-access", new EmergencyAccessRequest(""));
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);

        var granted = await unrelated.PostAsJsonAsync($"/api/clinical/patients/{patient.Id}/emergency-access", new EmergencyAccessRequest("مریض در حالت اضطرار"));
        Assert.Equal(HttpStatusCode.NoContent, granted.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await unrelated.GetAsync($"/api/consultations/{visit.Id}/chart")).StatusCode);

        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var actions = await db.AuditLogs.Where(a => a.UserId == unrelatedId && a.EntityId == patient.Id.ToString()).Select(a => a.Action).ToListAsync();
        Assert.Contains("ClinicalEmergencyAccess", actions);
        Assert.Contains("ClinicalRecordViewed", actions);
    }

    [Fact]
    public async Task Reception_cashier_and_admin_cannot_open_the_clinical_chart()
    {
        var (patient, visit) = await NewVisitAsync();

        foreach (var role in new[] { Roles.Receptionist, Roles.Cashier, Roles.SuperAdmin, Roles.Manager })
        {
            var client = await UserAsync(role);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/consultations/{visit.Id}/chart")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/clinical/patients/{patient.Id}/history")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/clinical/patients/{patient.Id}/emergency-access", new EmergencyAccessRequest("تلاش برای دور زدن"))).StatusCode);
        }
    }

    [Fact]
    public async Task Surgery_team_chart_hides_the_confidential_note()
    {
        var (_, visit) = await NewVisitAsync();
        var doctor = await UserAsync(Roles.Doctor);
        await doctor.PutAsJsonAsync($"/api/consultations/{visit.Id}", new SaveConsultationRequest(null, null, null, "خیلی محرمانه", null, null));

        var team = await UserAsync(Roles.Surgery);
        var chart = await team.GetFromJsonAsync<ChartDto>($"/api/consultations/{visit.Id}/chart", JsonOptions);
        Assert.Null(chart!.Consultation!.ConfidentialNote);
    }
}
