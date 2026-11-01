using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Patients;
using AlShifa.Api.Features.Users;
using AlShifa.Api.Features.Visits;
using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AlShifa.Tests;

[Collection("api")]
public sealed class ReceptionTests(ApiFixture api)
{
    private static string Name(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..20];

    private async Task<HttpClient> UserAsync(params string[] roles)
    {
        var name = Name("u");
        await api.CreateUserAsync(name, roles);
        return await api.LoginAsync(name);
    }

    private static SavePatientRequest NewPatient(string? firstName = null) =>
        new(firstName ?? Name("احمد"), "کریمی", "محمود", Gender.Male, null, 30, "0700" + Random.Shared.Next(100000, 999999), "کابل");

    private static HttpRequestMessage Post(string url, object body, string? key = null)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        message.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("N"));
        return message;
    }

    private static async Task<PatientDto> CreatePatientAsync(HttpClient client, SavePatientRequest? request = null)
    {
        var response = await client.SendAsync(Post("/api/patients", request ?? NewPatient()));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<PatientDto>())!;
    }

    private static async Task<int> DepartmentAsync(HttpClient client, string name = "پذیرش")
    {
        var departments = await client.GetFromJsonAsync<List<LookupItem>>("/api/lookups/departments");
        return (int)departments!.Single(d => d.Name == name).Id;
    }

    [Fact]
    public async Task Registered_patient_is_visible_to_triage_and_gets_a_file_number()
    {
        var reception = await UserAsync(Roles.Receptionist);
        var patient = await CreatePatientAsync(reception);
        Assert.StartsWith("P-", patient.FileNumber);

        var triage = await UserAsync(Roles.Triage);
        var seen = await triage.GetFromJsonAsync<PatientDto>($"/api/patients/{patient.Id}");
        Assert.Equal(patient.FileNumber, seen!.FileNumber);
    }

    [Fact]
    public async Task Users_without_permission_cannot_read_a_patient_by_guessing_the_id()
    {
        var reception = await UserAsync(Roles.Receptionist);
        var patient = await CreatePatientAsync(reception);

        foreach (var role in new[] { Roles.Accountant, Roles.Inventory })
        {
            var client = await UserAsync(role);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/patients/{patient.Id}")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await api.NewClient().GetAsync($"/api/patients/{patient.Id}")).StatusCode);
    }

    [Fact]
    public async Task Invalid_input_returns_400_not_500()
    {
        var reception = await UserAsync(Roles.Receptionist);

        var missingName = await reception.SendAsync(Post("/api/patients", NewPatient("")));
        Assert.Equal(HttpStatusCode.BadRequest, missingName.StatusCode);

        var badEnum = await reception.SendAsync(Post("/api/patients", new { firstName = "نام", gender = "NotAGender" }));
        Assert.Equal(HttpStatusCode.BadRequest, badEnum.StatusCode);

        var future = await reception.SendAsync(Post("/api/patients", NewPatient() with { BirthDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)) }));
        Assert.Equal(HttpStatusCode.BadRequest, future.StatusCode);
        Assert.Contains("invalid_birth_date", await future.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Gender_is_accepted_as_text_from_the_browser()
    {
        var reception = await UserAsync(Roles.Receptionist);
        var response = await reception.SendAsync(Post("/api/patients", new { firstName = Name("نام"), gender = "Female", estimatedAge = 20 }));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Possible_duplicate_needs_confirmation()
    {
        var reception = await UserAsync(Roles.Receptionist);
        var request = NewPatient();
        await CreatePatientAsync(reception, request);

        var second = await reception.SendAsync(Post("/api/patients", request));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var body = await second.Content.ReadAsStringAsync();
        Assert.Contains("possible_duplicate", body);
        Assert.Contains("matches", body);

        await CreatePatientAsync(reception, request with { ConfirmNotDuplicate = true });
    }

    [Fact]
    public async Task Same_idempotency_key_creates_one_patient_even_when_sent_concurrently()
    {
        var reception = await UserAsync(Roles.Receptionist);
        var request = NewPatient();
        var key = Guid.NewGuid().ToString("N");

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => reception.SendAsync(Post("/api/patients", request, key))));
        var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        Assert.Single(bodies.Select(b => JsonDocument.Parse(b).RootElement.GetProperty("id").GetInt64()).Distinct());

        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.Patients.CountAsync(p => p.FirstName == request.FirstName));
    }

    [Fact]
    public async Task Stale_patient_edit_is_rejected_and_keeps_the_first_change()
    {
        var reception = await UserAsync(Roles.Receptionist);
        var patient = await CreatePatientAsync(reception);
        var edit = NewPatient(patient.FirstName) with { Version = patient.Version };

        var first = await reception.PutAsJsonAsync($"/api/patients/{patient.Id}", edit with { Address = "هرات" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        var second = await reception.PutAsJsonAsync($"/api/patients/{patient.Id}", edit with { Address = "بلخ" });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains("concurrency_conflict", await second.Content.ReadAsStringAsync());

        var current = await reception.GetFromJsonAsync<PatientDto>($"/api/patients/{patient.Id}");
        Assert.Equal("هرات", current!.Address);

        var changes = await reception.GetFromJsonAsync<List<PatientChangeDto>>($"/api/patients/{patient.Id}/changes");
        Assert.Contains(changes!, c => c.Field == "Address" && c.NewValue == "هرات");
    }

    [Fact]
    public async Task Concurrent_visits_get_distinct_queue_numbers()
    {
        var reception = await UserAsync(Roles.Receptionist);
        var department = await DepartmentAsync(reception, "معاینه ابتدایی");
        var patients = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => CreatePatientAsync(reception)));

        var responses = await Task.WhenAll(patients.Select(p =>
            reception.SendAsync(Post("/api/visits", new CreateVisitRequest(p.Id, department, null, "درد چشم")))));

        var created = responses.Where(r => r.StatusCode == HttpStatusCode.Created).ToList();
        foreach (var response in responses.Except(created))
        {
            // A lock-wait loser reports a clear retryable conflict rather than a duplicate number.
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains("busy", await response.Content.ReadAsStringAsync());
        }

        var rows = await Task.WhenAll(created.Select(r => r.Content.ReadFromJsonAsync<VisitRow>()));
        Assert.NotEmpty(rows);
        Assert.Equal(rows.Length, rows.Select(r => r!.QueueNumber).Distinct().Count());
        Assert.Equal(rows.Length, rows.Select(r => r!.VisitNumber).Distinct().Count());
    }

    [Fact]
    public async Task Duplicate_active_visit_is_rejected_even_under_concurrency()
    {
        var reception = await UserAsync(Roles.Receptionist);
        var department = await DepartmentAsync(reception);
        var patient = await CreatePatientAsync(reception);

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            reception.SendAsync(Post("/api/visits", new CreateVisitRequest(patient.Id, department, null, null)))));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
    }

    [Fact]
    public async Task Visit_can_complete_without_any_diagnostic_step_and_cannot_reopen()
    {
        var reception = await UserAsync(Roles.Receptionist);
        var department = await DepartmentAsync(reception);
        var patient = await CreatePatientAsync(reception);

        var created = await reception.SendAsync(Post("/api/visits", new CreateVisitRequest(patient.Id, department, null, null)));
        var visit = (await created.Content.ReadFromJsonAsync<VisitRow>())!;
        Assert.Equal(nameof(VisitStatus.Registered), visit.Status);

        var done = await reception.PostAsJsonAsync($"/api/visits/{visit.Id}/status", new ChangeVisitStatusRequest(VisitStatus.Completed, visit.Version));
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        var completed = (await done.Content.ReadFromJsonAsync<VisitRow>())!;

        var reopen = await reception.PostAsJsonAsync($"/api/visits/{visit.Id}/status", new ChangeVisitStatusRequest(VisitStatus.Waiting, completed.Version));
        Assert.Equal(HttpStatusCode.Conflict, reopen.StatusCode);
        Assert.Contains("invalid_transition", await reopen.Content.ReadAsStringAsync());

        // Closed visit no longer blocks a new one the same day.
        var again = await reception.SendAsync(Post("/api/visits", new CreateVisitRequest(patient.Id, department, null, null)));
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
    }

    [Fact]
    public async Task Triage_can_move_a_visit_but_not_cancel_it()
    {
        var reception = await UserAsync(Roles.Receptionist);
        var department = await DepartmentAsync(reception);
        var patient = await CreatePatientAsync(reception);
        var created = await reception.SendAsync(Post("/api/visits", new CreateVisitRequest(patient.Id, department, null, null)));
        var visit = (await created.Content.ReadFromJsonAsync<VisitRow>())!;

        var triage = await UserAsync(Roles.Triage);
        var cancel = await triage.PostAsJsonAsync($"/api/visits/{visit.Id}/status", new ChangeVisitStatusRequest(VisitStatus.Cancelled, visit.Version));
        Assert.Equal(HttpStatusCode.Forbidden, cancel.StatusCode);

        var move = await triage.PostAsJsonAsync($"/api/visits/{visit.Id}/status", new ChangeVisitStatusRequest(VisitStatus.InPreExam, visit.Version));
        Assert.Equal(HttpStatusCode.OK, move.StatusCode);
    }

    [Fact]
    public async Task Outbox_events_for_a_visit_carry_only_identifiers()
    {
        var reception = await UserAsync(Roles.Receptionist);
        var department = await DepartmentAsync(reception);
        var patient = await CreatePatientAsync(reception, NewPatient("محرمانه" + Guid.NewGuid().ToString("N")[..8]));
        var created = await reception.SendAsync(Post("/api/visits", new CreateVisitRequest(patient.Id, department, null, "علت خصوصی")));
        var visit = (await created.Content.ReadFromJsonAsync<VisitRow>())!;

        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var payloads = await db.OutboxEvents.Where(e => e.Payload.Contains($":{visit.Id}}}")).Select(e => e.Payload).ToListAsync();

        Assert.NotEmpty(payloads);
        Assert.All(payloads, p =>
        {
            Assert.DoesNotContain(patient.FirstName, p);
            Assert.DoesNotContain("علت خصوصی", p);
        });
    }

    [Fact]
    public async Task Appointment_slot_cannot_be_double_booked_and_check_in_creates_a_visit()
    {
        var admin = await UserAsync(Roles.SuperAdmin);
        var staff = await admin.PostAsJsonAsync("/api/staff", new SaveStaffRequest("داکتر " + Name("x"), null, true, true, null, null));
        Assert.Equal(HttpStatusCode.Created, staff.StatusCode);
        var doctorId = JsonDocument.Parse(await staff.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt64();

        var reception = await UserAsync(Roles.Receptionist);
        var patientA = await CreatePatientAsync(reception);
        var patientB = await CreatePatientAsync(reception);
        var at = DateTime.UtcNow.AddDays(1).Date.AddHours(5);

        var responses = await Task.WhenAll(new[] { patientA, patientB }.Select(p =>
            reception.SendAsync(Post("/api/appointments", new CreateAppointmentRequest(p.Id, doctorId, at, null)))));
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);

        var department = await DepartmentAsync(reception, "داکتر");
        var day = DateOnly.FromDateTime(at.AddHours(4.5));
        var list = await reception.GetFromJsonAsync<List<AppointmentRow>>($"/api/appointments?date={day:yyyy-MM-dd}");
        var appointment = list!.Single(a => a.DoctorStaffId == doctorId);

        var checkIn = await reception.SendAsync(Post($"/api/appointments/{appointment.Id}/check-in", new CheckInRequest(department, appointment.Version)));
        Assert.True(checkIn.StatusCode == HttpStatusCode.Created, await checkIn.Content.ReadAsStringAsync());
        var visit = (await checkIn.Content.ReadFromJsonAsync<VisitRow>())!;
        Assert.Equal(appointment.PatientId, visit.PatientId);
    }
}
