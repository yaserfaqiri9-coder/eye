using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Calls;
using AlShifa.Api.Features.Clinical;
using AlShifa.Api.Features.Finance;
using AlShifa.Api.Features.Patients;
using AlShifa.Api.Features.Users;
using AlShifa.Api.Features.Visits;
using AlShifa.Api.Infrastructure;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AlShifa.Tests;

[Collection("api")]
public sealed class CallTests(ApiFixture api)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string VoiceRoot = Path.Combine(Path.GetTempPath(), "alshifa-test-storage", "voice");

    private static string Name(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..20];

    private async Task<HttpClient> UserAsync(params string[] roles)
    {
        var name = Name("u");
        await api.CreateUserAsync(name, roles);
        return await api.LoginAsync(name);
    }

    private static HttpRequestMessage Keyed(string url, object body) => ApiFixture.Keyed(url, body);

    private static async Task<T> Read<T>(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, text);
        return JsonSerializer.Deserialize<T>(text, JsonOptions)!;
    }

    private static async Task AssertRefused(HttpResponseMessage response, string code, HttpStatusCode status = HttpStatusCode.Conflict)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, text);
        Assert.Contains(code, text);
    }

    private async Task<T> InDbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        using var scope = api.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    /// <summary>A clean speaker for each test: an active station, nothing left in the air and every clip present.</summary>
    private async Task<(HttpClient Client, string StationId)> StationAsync()
    {
        Directory.CreateDirectory(VoiceRoot);
        foreach (var clip in VoicePack.AllClips())
        {
            var path = Path.Combine(VoiceRoot, clip);
            if (!File.Exists(path))
            {
                await File.WriteAllBytesAsync(path, [0xFF, 0xFB]);
            }
        }

        await InDbAsync(db => db.PatientCalls
            .Where(c => c.Status == CallStatus.Queued || c.Status == CallStatus.Playing)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, CallStatus.Cancelled)));

        var client = await UserAsync(Roles.AudioStation);
        var id = Guid.NewGuid().ToString("N");
        var claimed = await client.PostAsJsonAsync("/api/calls/station/claim", new ClaimStationRequest(id, true));
        Assert.True(claimed.IsSuccessStatusCode, await claimed.Content.ReadAsStringAsync());
        return (client, id);
    }

    private static async Task<NextCallDto?> NextAsync(HttpClient station, string id)
    {
        var response = await station.PostAsJsonAsync("/api/calls/station/next", new NextCallRequest(id));
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }

        return await Read<NextCallDto>(response);
    }

    private static Task<HttpResponseMessage> Finish(HttpClient station, string id, long callId, bool played = true) =>
        station.PostAsJsonAsync($"/api/calls/{callId}/result", new CallResultRequest(id, played, played ? null : "خطا"));

    private async Task<(PatientDto Patient, VisitRow Visit)> NewVisitAsync(long? doctorStaffId = null, string department = "triage")
    {
        var reception = await api.FeeDeskAsync();
        var patient = await Read<PatientDto>(await reception.SendAsync(Keyed("/api/patients",
            new SavePatientRequest(Name("احمد"), null, "محمود", Gender.Male, null, 30, null, null))), HttpStatusCode.Created);
        var visit = await Read<VisitRow>(await reception.SendAsync(Keyed("/api/visits",
            new CreateVisitRequest(patient.Id, await api.DepartmentIdAsync(department), doctorStaffId, null, null, await api.PaidTokenAsync(department)))), HttpStatusCode.Created);
        return (patient, visit);
    }

    private async Task<(HttpClient Client, long StaffId)> DoctorInRoomAsync(int room)
    {
        var name = Name("doc");
        var userId = await api.CreateUserAsync(name, Roles.Doctor);
        var client = await api.LoginAsync(name);
        var staffId = await InDbAsync(async db =>
        {
            var staff = new Staff { FullName = name, IsDoctor = true, UserId = userId };
            db.Staff.Add(staff);
            await db.SaveChangesAsync();
            return staff.Id;
        });

        var admin = await UserAsync(Roles.SuperAdmin);
        var saved = await admin.PutAsJsonAsync("/api/calls/settings", new SaveCallSettingsRequest([], [new CallDoctorRoom(staffId, room)]));
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
        return (client, staffId);
    }

    private static Task<HttpResponseMessage> Call(HttpClient client, CallDesk desk, long visitId, bool recall = false) =>
        client.PostAsJsonAsync("/api/calls", new CreateCallRequest(desk, visitId, null, recall));

    [Fact]
    public async Task A_call_reaches_the_speaker_once_with_number_and_place_only_and_changes_nothing_else()
    {
        var (station, stationId) = await StationAsync();
        var (patient, visit) = await NewVisitAsync();
        var triage = await UserAsync(Roles.Triage);
        var invoicesBefore = await InDbAsync(db => db.Invoices.CountAsync());
        var visitsBefore = await InDbAsync(db => db.Visits.CountAsync());

        var call = await Read<CallRow>(await Call(triage, CallDesk.Vision, visit.Id), HttpStatusCode.Created);
        Assert.Equal("Queued", call.Status);

        var raw = await station.PostAsJsonAsync("/api/calls/station/next", new NextCallRequest(stationId));
        var text = await raw.Content.ReadAsStringAsync();
        Assert.DoesNotContain(patient.FirstName, text);
        Assert.DoesNotContain(patient.FileNumber, text);
        var next = JsonSerializer.Deserialize<NextCallDto>(text, JsonOptions)!;
        Assert.Equal([$"number-{visit.QueueNumber:D4}.mp3", "to-vision.mp3"], next.Clips);

        // Nothing else is handed out while one is playing, so announcements cannot overlap.
        await AssertRefused(await Call(triage, CallDesk.Vision, visit.Id), "already_calling");
        Assert.Null(await NextAsync(station, stationId));

        Assert.True((await Finish(station, stationId, next.Id)).IsSuccessStatusCode);
        Assert.Null(await NextAsync(station, stationId));

        // No medical or financial state moved, and a call opens neither a visit nor an invoice.
        var after = await InDbAsync(db => db.Visits.AsNoTracking().SingleAsync(v => v.Id == visit.Id));
        Assert.Equal((visit.Status, visit.Version), (after.Status.ToString(), after.Version));
        Assert.Equal(invoicesBefore, await InDbAsync(db => db.Invoices.CountAsync()));
        Assert.Equal(visitsBefore, await InDbAsync(db => db.Visits.CountAsync()));

        var view = await Read<CallDeskView>(await triage.GetAsync("/api/calls/desk/Vision"));
        Assert.Equal("Played", view.Rows.Single(r => r.VisitId == visit.Id).LastCall!.Status);

        // The recall says "again", and the history records who called for which visit.
        await Read<CallRow>(await Call(triage, CallDesk.Vision, visit.Id, recall: true), HttpStatusCode.Created);
        Assert.Equal("again-vision.mp3", (await NextAsync(station, stationId))!.Clips[1]);
        var audit = await InDbAsync(db => db.AuditLogs.AsNoTracking().Where(a => a.Action == "PatientRecalled" && a.EntityId == visit.Id.ToString()).SingleAsync());
        Assert.Contains(visit.VisitNumber, audit.Details);
        Assert.NotNull(audit.UserId);
    }

    [Fact]
    public async Task A_transfer_only_enters_the_queue_and_never_calls_by_itself()
    {
        var (station, stationId) = await StationAsync();
        var (doctor, staffId) = await DoctorInRoomAsync(3);
        var (_, visit) = await NewVisitAsync(staffId);
        var triage = await UserAsync(Roles.Triage);

        await AssertRefused(await Call(doctor, CallDesk.Doctor, visit.Id), "not_in_queue");

        await api.CompleteVisionAsync(visit.Id);
        Assert.Null(await NextAsync(station, stationId));
        Assert.Equal(0, await InDbAsync(db => db.PatientCalls.CountAsync(c => c.VisitId == visit.Id)));

        await AssertRefused(await Call(triage, CallDesk.Vision, visit.Id), "not_in_queue");
        await Read<CallRow>(await Call(doctor, CallDesk.Doctor, visit.Id), HttpStatusCode.Created);
        Assert.Equal("to-room-03.mp3", (await NextAsync(station, stationId))!.Clips[1]);
    }

    [Fact]
    public async Task Paid_desks_wait_for_valid_payment_and_the_patient_returns_to_the_same_doctor()
    {
        var (station, stationId) = await StationAsync();
        var (doctor, staffId) = await DoctorInRoomAsync(4);
        var (patient, visit) = await NewVisitAsync(staffId);
        await api.CompleteVisionAsync(visit.Id);
        var triage = await UserAsync(Roles.Triage);

        var tests = await doctor.GetFromJsonAsync<JsonElement>("/api/diagnostic-tests");
        var testId = tests.EnumerateArray().First(t => t.GetProperty("kind").GetString() == "Refraction").GetProperty("id").GetInt32();
        await Read<DiagnosticOrderDto>(await doctor.SendAsync(Keyed("/api/diagnostic-orders", new CreateDiagnosticOrderRequest(patient.Id, visit.Id, testId, null))), HttpStatusCode.Created);

        await AssertRefused(await Call(triage, CallDesk.Refraction, visit.Id), "not_in_queue");
        await AssertRefused(await Call(doctor, CallDesk.Doctor, visit.Id), "not_in_queue");

        var desk = await api.FeeDeskAsync();
        var fees = await desk.GetFromJsonAsync<List<ReceptionFeeRow>>("/api/reception-fees", JsonOptions);
        var fee = fees!.Last();
        await Read<JsonElement>(await desk.SendAsync(Keyed("/api/reception-fees/pay", new PaymentRequest(fee.InvoiceId, fee.Balance, PaymentMethod.Cash, null, fee.InvoiceVersion))), HttpStatusCode.Created);

        var called = await Read<CallRow>(await Call(triage, CallDesk.Refraction, visit.Id), HttpStatusCode.Created);
        var next = await NextAsync(station, stationId);
        Assert.Equal(called.Id, next!.Id);
        Assert.Equal("to-refraction.mp3", next.Clips[1]);
        Assert.True((await Finish(station, stationId, next.Id)).IsSuccessStatusCode);

        // The doctor still waits for the answer, so the patient cannot be called back yet.
        await AssertRefused(await Call(doctor, CallDesk.Doctor, visit.Id), "not_in_queue");

        var saved = await triage.PutAsJsonAsync($"/api/refractions/{visit.Id}", new SaveRefractionRequest("6/18", null, null, null, null, null, null, null, null, null, null));
        Assert.True(saved.IsSuccessStatusCode, await saved.Content.ReadAsStringAsync());
        Assert.True((await triage.PostAsync($"/api/refractions/{visit.Id}/complete", null)).IsSuccessStatusCode);

        await AssertRefused(await Call(triage, CallDesk.Refraction, visit.Id), "not_in_queue");
        var other = await DoctorInRoomAsync(5);
        await AssertRefused(await Call(other.Client, CallDesk.Doctor, visit.Id), "not_responsible_doctor", HttpStatusCode.Forbidden);
        await Read<CallRow>(await Call(doctor, CallDesk.Doctor, visit.Id), HttpStatusCode.Created);
        Assert.Equal("to-room-04.mp3", (await NextAsync(station, stationId))!.Clips[1]);
    }

    [Fact]
    public async Task Only_the_desk_may_call_and_only_the_station_may_play()
    {
        var (station, stationId) = await StationAsync();
        var (_, visit) = await NewVisitAsync();
        var pharmacist = await UserAsync(Roles.Pharmacist);
        var triage = await UserAsync(Roles.Triage);
        var manager = await UserAsync(Roles.Manager);

        await AssertRefused(await Call(pharmacist, CallDesk.Vision, visit.Id), "", HttpStatusCode.Forbidden);
        await AssertRefused(await Call(manager, CallDesk.Vision, visit.Id), "", HttpStatusCode.Forbidden);
        await AssertRefused(await Call(station, CallDesk.Vision, visit.Id), "", HttpStatusCode.Forbidden);
        Assert.Equal(HttpStatusCode.Forbidden, (await pharmacist.GetAsync("/api/calls/desk/Vision")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await station.GetAsync("/api/calls/desk/Vision")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await triage.PostAsJsonAsync("/api/calls/station/next", new NextCallRequest(stationId))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await triage.PostAsJsonAsync("/api/calls/station/claim", new ClaimStationRequest("x", true))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await triage.GetAsync("/api/calls/voice/to-vision.mp3")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.NewClient().GetAsync("/api/calls/voice/to-vision.mp3")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await station.GetAsync("/api/calls/voice/..%2F..%2Fsecret.mp3")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await station.GetAsync("/api/calls/voice/to-vision.mp3")).StatusCode);

        // The station's history shows numbers and staff, never who the patient is.
        var status = await station.GetStringAsync($"/api/calls/station/status?stationId={stationId}");
        Assert.DoesNotContain(visit.PatientName, status);
    }

    [Fact]
    public async Task Simultaneous_presses_call_a_patient_once_and_different_patients_play_one_by_one_in_order()
    {
        var (station, stationId) = await StationAsync();
        var triage = await UserAsync(Roles.Triage);
        var (_, one) = await NewVisitAsync();

        var presses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Call(triage, CallDesk.Vision, one.Id)));
        Assert.Single(presses, p => p.StatusCode == HttpStatusCode.Created);
        Assert.All(presses.Where(p => p.StatusCode != HttpStatusCode.Created), p => Assert.Equal(HttpStatusCode.Conflict, p.StatusCode));
        Assert.Equal(1, await InDbAsync(db => db.PatientCalls.CountAsync(c => c.VisitId == one.Id)));

        var others = new List<VisitRow>();
        for (var i = 0; i < 5; i++)
        {
            others.Add((await NewVisitAsync()).Visit);
        }

        var calls = await Task.WhenAll(others.Select(v => Call(triage, CallDesk.Vision, v.Id)));
        Assert.All(calls, c => Assert.Equal(HttpStatusCode.Created, c.StatusCode));

        var played = new List<long>();
        while (await NextAsync(station, stationId) is { } next)
        {
            played.Add(next.Id);
            Assert.Null(await NextAsync(station, stationId));
            Assert.True((await Finish(station, stationId, next.Id)).IsSuccessStatusCode);
        }

        Assert.Equal(6, played.Count);
        Assert.Equal(played.Order(), played);
        Assert.Equal(played.Count, played.Distinct().Count());
    }

    [Fact]
    public async Task After_an_outage_nothing_is_repeated_and_old_calls_stay_silent_until_someone_recalls()
    {
        var (station, stationId) = await StationAsync();
        var triage = await UserAsync(Roles.Triage);
        var (_, claimed) = await NewVisitAsync();
        var (_, waiting) = await NewVisitAsync();

        // The station took this one and lost the network before it could report back.
        var first = await Read<CallRow>(await Call(triage, CallDesk.Vision, claimed.Id), HttpStatusCode.Created);
        Assert.Equal(first.Id, (await NextAsync(station, stationId))!.Id);
        var second = await Read<CallRow>(await Call(triage, CallDesk.Vision, waiting.Id), HttpStatusCode.Created);

        var longAgo = DateTime.UtcNow.AddMinutes(-10);
        await InDbAsync(db => db.PatientCalls.Where(c => c.Id == first.Id || c.Id == second.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.RequestedAt, longAgo).SetProperty(c => c.ClaimedAt, c => c.ClaimedAt == null ? null : longAgo)));

        // Staff see the failure at once, even before the station is back.
        var view = await Read<CallDeskView>(await triage.GetAsync("/api/calls/desk/Vision"));
        Assert.Equal("Failed", view.Rows.Single(r => r.VisitId == waiting.Id).LastCall!.Status);

        Assert.Null(await NextAsync(station, stationId));
        Assert.Null(await NextAsync(station, stationId));
        Assert.Equal(CallStatus.Failed, await InDbAsync(db => db.PatientCalls.Where(c => c.Id == first.Id).Select(c => c.Status).SingleAsync()));
        Assert.Equal(CallStatus.Failed, await InDbAsync(db => db.PatientCalls.Where(c => c.Id == second.Id).Select(c => c.Status).SingleAsync()));

        // A late report from the old attempt cannot revive it, and a person's recall works normally.
        Assert.True((await Finish(station, stationId, first.Id)).IsSuccessStatusCode);
        Assert.Equal(CallStatus.Failed, await InDbAsync(db => db.PatientCalls.Where(c => c.Id == first.Id).Select(c => c.Status).SingleAsync()));
        var recall = await Read<CallRow>(await Call(triage, CallDesk.Vision, waiting.Id, recall: true), HttpStatusCode.Created);
        Assert.Equal(recall.Id, (await NextAsync(station, stationId))!.Id);
    }

    [Fact]
    public async Task One_station_at_a_time_and_no_call_without_a_working_station_or_clip()
    {
        var (first, firstId) = await StationAsync();
        var triage = await UserAsync(Roles.Triage);
        var (_, visit) = await NewVisitAsync();

        var secondId = Guid.NewGuid().ToString("N");
        var second = await UserAsync(Roles.AudioStation);
        await AssertRefused(await second.PostAsJsonAsync("/api/calls/station/claim", new ClaimStationRequest(secondId, false)), "station_busy");
        Assert.Null(await NextAsync(first, firstId));
        await AssertRefused(await second.PostAsJsonAsync("/api/calls/station/next", new NextCallRequest(secondId)), "station_replaced");

        var takeOver = await second.PostAsJsonAsync("/api/calls/station/claim", new ClaimStationRequest(secondId, true));
        Assert.True(takeOver.IsSuccessStatusCode);
        await AssertRefused(await first.PostAsJsonAsync("/api/calls/station/next", new NextCallRequest(firstId)), "station_replaced");

        var clip = Path.Combine(VoiceRoot, "to-vision.mp3");
        File.Delete(clip);
        try
        {
            await AssertRefused(await Call(triage, CallDesk.Vision, visit.Id), "voice_clip_missing");
            var status = await Read<StationStatus>(await second.GetAsync($"/api/calls/station/status?stationId={secondId}"));
            Assert.True(status.Active);
            Assert.Equal(1, status.ClipsMissing);
            Assert.Equal(HttpStatusCode.NotFound, (await second.GetAsync("/api/calls/voice/to-vision.mp3")).StatusCode);
        }
        finally
        {
            await File.WriteAllBytesAsync(clip, [0xFF, 0xFB]);
        }

        var stale = DateTime.UtcNow.AddMinutes(-10).Ticks;
        await InDbAsync(db => db.Settings.Where(s => s.Key == "calls.station").ExecuteUpdateAsync(s => s.SetProperty(x => x.Value, $"{secondId}|{stale}")));
        await AssertRefused(await Call(triage, CallDesk.Vision, visit.Id), "no_audio_station");
        Assert.False((await Read<CallDeskView>(await triage.GetAsync("/api/calls/desk/Vision"))).StationOnline);
    }

    [Fact]
    public async Task Attendance_is_recorded_without_moving_the_patient_and_a_waiting_announcement_is_withdrawn()
    {
        var (station, stationId) = await StationAsync();
        var triage = await UserAsync(Roles.Triage);
        var (_, visit) = await NewVisitAsync();

        await AssertRefused(await Call(triage, CallDesk.Vision, visit.Id, recall: true), "no_previous_call");
        var call = await Read<CallRow>(await Call(triage, CallDesk.Vision, visit.Id), HttpStatusCode.Created);

        var pharmacist = await UserAsync(Roles.Pharmacist);
        Assert.Equal(HttpStatusCode.Forbidden, (await pharmacist.PostAsJsonAsync($"/api/calls/{call.Id}/outcome", new CallOutcomeRequest(CallOutcome.Attended))).StatusCode);

        var attended = await Read<CallRow>(await triage.PostAsJsonAsync($"/api/calls/{call.Id}/outcome", new CallOutcomeRequest(CallOutcome.Attended)));
        Assert.Equal(("Cancelled", "Attended"), (attended.Status, attended.Outcome));
        Assert.Null(await NextAsync(station, stationId));

        var after = await InDbAsync(db => db.Visits.AsNoTracking().SingleAsync(v => v.Id == visit.Id));
        Assert.Equal((visit.Status, visit.Version), (after.Status.ToString(), after.Version));

        var again = await Read<CallRow>(await Call(triage, CallDesk.Vision, visit.Id, recall: true), HttpStatusCode.Created);
        var noShow = await Read<CallRow>(await triage.PostAsJsonAsync($"/api/calls/{again.Id}/outcome", new CallOutcomeRequest(CallOutcome.NoShow)));
        Assert.Equal("NoShow", noShow.Outcome);
    }

    [Fact]
    public async Task Reception_calls_an_unregistered_token_and_rooms_come_from_settings()
    {
        var (station, stationId) = await StationAsync();
        var desk = await api.FeeDeskAsync();
        var token = await api.NewTokenAsync();

        var view = await Read<CallDeskView>(await desk.GetAsync("/api/calls/desk/Reception"));
        Assert.Contains(view.Rows, r => r.TokenId == token.Id && r.QueueNumber == token.QueueNumber);

        await Read<CallRow>(await desk.PostAsJsonAsync("/api/calls", new CreateCallRequest(CallDesk.Reception, null, token.Id, false)), HttpStatusCode.Created);
        Assert.Equal("to-reception.mp3", (await NextAsync(station, stationId))!.Clips[1]);

        var admin = await UserAsync(Roles.SuperAdmin);
        await AssertRefused(await admin.PutAsJsonAsync("/api/calls/settings", new SaveCallSettingsRequest([new CallDeskRoom(CallDesk.Vision, 99)], [])), "invalid_room", HttpStatusCode.BadRequest);
        Assert.True((await admin.PutAsJsonAsync("/api/calls/settings", new SaveCallSettingsRequest([new CallDeskRoom(CallDesk.Vision, 7)], []))).IsSuccessStatusCode);
        try
        {
            var (_, visit) = await NewVisitAsync();
            var triage = await UserAsync(Roles.Triage);
            var stationClear = await NextAsync(station, stationId);
            if (stationClear is not null)
            {
                await Finish(station, stationId, stationClear.Id);
            }

            await InDbAsync(db => db.PatientCalls.Where(c => c.Status == CallStatus.Playing).ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, CallStatus.Played)));
            await Read<CallRow>(await Call(triage, CallDesk.Vision, visit.Id), HttpStatusCode.Created);
            Assert.Equal("to-room-07.mp3", (await NextAsync(station, stationId))!.Clips[1]);
        }
        finally
        {
            await admin.PutAsJsonAsync("/api/calls/settings", new SaveCallSettingsRequest([new CallDeskRoom(CallDesk.Vision, null)], []));
        }
    }

    [Fact]
    public async Task A_visit_keeps_one_number_and_no_two_patients_share_a_number_across_departments()
    {
        var triageToken = await api.NewTokenAsync("triage");
        var doctorToken = await api.NewTokenAsync("doctor");
        Assert.NotEqual(triageToken.QueueNumber, doctorToken.QueueNumber);

        // The department changes at registration; the number on the patient's slip does not.
        var desk = await api.FeeDeskAsync();
        var patient = await Read<PatientDto>(await desk.SendAsync(Keyed("/api/patients", new SavePatientRequest(Name("احمد"), null, "محمود", Gender.Male, null, 30, null, null))), HttpStatusCode.Created);
        var paid = await desk.SendAsync(Keyed("/api/reception-fees/pay", new PaymentRequest(doctorToken.InvoiceId, doctorToken.Balance, PaymentMethod.Cash, null, doctorToken.InvoiceVersion)));
        Assert.True(paid.IsSuccessStatusCode);
        var visit = await Read<VisitRow>(await desk.SendAsync(Keyed("/api/visits", new CreateVisitRequest(patient.Id, await api.DepartmentIdAsync("triage"), null, null, null, doctorToken.Id))), HttpStatusCode.Created);
        Assert.Equal(doctorToken.QueueNumber, visit.QueueNumber);
    }

    [Fact]
    public void The_pack_tool_produces_exactly_the_places_the_api_announces()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir!.FullName, "deploy", "windows", "voice-phrases.json")))
        {
            dir = dir.Parent;
        }

        using var phrases = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.FullName, "deploy", "windows", "voice-phrases.json")));
        var root = phrases.RootElement;
        Assert.Equal(VoicePack.MaxNumber, root.GetProperty("maxNumber").GetInt32());
        Assert.Equal(VoicePack.MaxRoom, root.GetProperty("maxRoom").GetInt32());
        Assert.Equal(
            VoicePack.Sections.Values.ToDictionary(s => s.Clip, s => s.Label).OrderBy(s => s.Key),
            root.GetProperty("sections").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!).OrderBy(s => s.Key));
    }

    [Fact]
    public async Task The_station_gets_a_live_signal_without_patient_data()
    {
        var (_, stationId) = await StationAsync();
        var name = Name("st");
        await api.CreateUserAsync(name, Roles.AudioStation);
        var baseUri = new Uri("https://localhost");
        var cookies = new CookieContainerHandler();
        var client = api.CreateDefaultClient(baseUri, cookies);
        await ApiFixture.RefreshCsrfAsync(client);
        Assert.True((await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(name, ApiFixture.TestPassword))).IsSuccessStatusCode);

        var events = new ConcurrentQueue<(string Type, string Payload)>();
        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(baseUri, "/hubs/events"), HttpTransportType.LongPolling, options =>
            {
                options.HttpMessageHandlerFactory = _ => api.Server.CreateHandler();
                options.Headers["Cookie"] = cookies.Container.GetCookieHeader(baseUri);
            })
            .Build();
        hub.On<JsonElement>("event", e => events.Enqueue((e.GetProperty("type").GetString()!, e.GetProperty("payload").GetString()!)));
        await hub.StartAsync();

        var (patient, visit) = await NewVisitAsync();
        var triage = await UserAsync(Roles.Triage);
        await Read<CallRow>(await Call(triage, CallDesk.Vision, visit.Id), HttpStatusCode.Created);

        var until = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < until && !events.Any(e => e.Type == "PatientCallRequested"))
        {
            await Task.Delay(100);
        }

        var signal = Assert.Single(events, e => e.Type == "PatientCallRequested");
        Assert.DoesNotContain(patient.FirstName, signal.Payload);
        Assert.DoesNotContain(visit.Id.ToString(), signal.Payload);
        Assert.NotEmpty(stationId);
    }
}
