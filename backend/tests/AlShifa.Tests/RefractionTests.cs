using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Clinical;
using AlShifa.Api.Features.Finance;
using AlShifa.Api.Features.Patients;
using AlShifa.Api.Features.Visits;

namespace AlShifa.Tests;

[Collection("api")]
public sealed class RefractionTests(ApiFixture api)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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

    private static async Task<T> Read<T>(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, text);
        return JsonSerializer.Deserialize<T>(text, JsonOptions)!;
    }

    private async Task<(PatientDto Patient, VisitRow Visit)> NewVisitAsync()
    {
        var reception = await UserAsync(Roles.Receptionist);
        var patient = await Read<PatientDto>(await reception.SendAsync(Post("/api/patients",
            new SavePatientRequest(Name("احمد"), null, "محمود", Gender.Male, null, 30, null, null))), HttpStatusCode.Created);
        var departments = await reception.GetFromJsonAsync<List<LookupItem>>("/api/lookups/departments");
        var visit = await Read<VisitRow>(await reception.SendAsync(Post("/api/visits",
            new CreateVisitRequest(patient.Id, (int)departments!.Single(d => d.Name == "معاینه ابتدایی").Id, null, null, null, await api.PaidTokenAsync()))), HttpStatusCode.Created);
        await api.CompleteVisionAsync(visit.Id);
        await RequestAndPayAsync(patient.Id, visit.Id);
        return (patient, visit);
    }

    private async Task RequestAndPayAsync(long patientId, long visitId)
    {
        var doctor = await UserAsync(Roles.Doctor);
        var tests = await doctor.GetFromJsonAsync<JsonElement>("/api/diagnostic-tests");
        var testId = tests.EnumerateArray().First(t => t.GetProperty("kind").GetString() == "Refraction").GetProperty("id").GetInt32();
        var order = await Read<DiagnosticOrderDto>(await doctor.SendAsync(Post("/api/diagnostic-orders", new CreateDiagnosticOrderRequest(patientId, visitId, testId, null))), HttpStatusCode.Created);

        var fileNumber = (await doctor.GetFromJsonAsync<PatientDto>($"/api/patients/{patientId}", JsonOptions))!.FileNumber;
        await api.PayFeeAsync(await api.OrderFeeAsync(fileNumber, order.TestName));
    }

    private static SaveRefractionRequest Sample(int? version, string finalSphere = "-1.25") => new(
        "6/18", "6/9", "6/9", null, null, null, "62", "K1 43.25", "یادداشت",
        [
            new RefractionLineInput(RefractionStage.Auto, EyeSide.OD, "-1.50", "-0.50", "90", null, null),
            new RefractionLineInput(RefractionStage.Final, EyeSide.OD, finalSphere, "-0.50", "90", "+1.00", "6/6"),
            new RefractionLineInput(RefractionStage.Final, EyeSide.OS, null, null, null, null, null),
        ],
        version);

    [Fact]
    public async Task Stages_stay_separate_and_blank_rows_are_not_stored()
    {
        var (patient, visit) = await NewVisitAsync();
        var triage = await UserAsync(Roles.Triage);

        var saved = await Read<RefractionDto>(await triage.PutAsJsonAsync($"/api/refractions/{visit.Id}", Sample(null)));

        Assert.Equal(2, saved.Lines.Count);
        Assert.Equal("-1.50", saved.Lines.Single(l => l.Stage == "Auto" && l.Eye == "OD").Sphere);
        Assert.Equal("-1.25", saved.Lines.Single(l => l.Stage == "Final" && l.Eye == "OD").Sphere);
        Assert.Equal("6/6", saved.Lines.Single(l => l.Stage == "Final" && l.Eye == "OD").Va);
        Assert.DoesNotContain(saved.Lines, l => l.Eye == "OS");

        var doctor = await UserAsync(Roles.Doctor);
        var read = await Read<RefractionDto>(await doctor.GetAsync($"/api/refractions/{visit.Id}"));
        Assert.Equal("62", read.Pd);
        var list = await doctor.GetFromJsonAsync<List<RefractionDto>>($"/api/clinical/patients/{patient.Id}/refractions", JsonOptions);
        Assert.Contains(list!, r => r.VisitId == visit.Id);
    }

    [Fact]
    public async Task Saving_again_replaces_lines_and_a_stale_version_is_refused()
    {
        var (_, visit) = await NewVisitAsync();
        var triage = await UserAsync(Roles.Triage);
        var first = await Read<RefractionDto>(await triage.PutAsJsonAsync($"/api/refractions/{visit.Id}", Sample(null)));

        var second = await Read<RefractionDto>(await triage.PutAsJsonAsync($"/api/refractions/{visit.Id}", Sample(first.Version, "-2.00")));
        Assert.Equal("-2.00", second.Lines.Single(l => l.Stage == "Final" && l.Eye == "OD").Sphere);
        Assert.Equal(2, second.Lines.Count);

        var stale = await triage.PutAsJsonAsync($"/api/refractions/{visit.Id}", Sample(first.Version, "-3.00"));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task Completing_locks_the_record_and_returns_the_patient_to_the_doctor()
    {
        var (_, visit) = await NewVisitAsync();
        var triage = await UserAsync(Roles.Triage);
        var saved = await Read<RefractionDto>(await triage.PutAsJsonAsync($"/api/refractions/{visit.Id}", Sample(null)));

        Assert.Equal(HttpStatusCode.NoContent, (await triage.PostAsync($"/api/refractions/{visit.Id}/complete", null)).StatusCode);

        var locked = await triage.PutAsJsonAsync($"/api/refractions/{visit.Id}", Sample(saved.Version + 1));
        Assert.Equal(HttpStatusCode.Conflict, locked.StatusCode);

        var after = await triage.GetFromJsonAsync<VisitRow>($"/api/visits/{visit.Id}", JsonOptions);
        Assert.Equal(nameof(VisitStatus.ReadyForDoctor), after!.Status);
    }

    [Fact]
    public async Task Only_pre_exam_roles_can_write_and_reception_cannot_read()
    {
        var (_, visit) = await NewVisitAsync();
        var doctor = await UserAsync(Roles.Doctor);
        var reception = await UserAsync(Roles.Receptionist);

        Assert.Equal(HttpStatusCode.Forbidden, (await doctor.PutAsJsonAsync($"/api/refractions/{visit.Id}", Sample(null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reception.GetAsync($"/api/refractions/{visit.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await doctor.GetAsync($"/api/refractions/{visit.Id}")).StatusCode);
    }
}
