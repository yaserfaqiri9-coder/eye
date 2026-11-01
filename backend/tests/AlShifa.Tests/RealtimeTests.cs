using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Patients;
using AlShifa.Api.Features.Users;
using AlShifa.Api.Features.Visits;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.Http.Connections;

namespace AlShifa.Tests;

[Collection("api")]
public sealed class RealtimeTests(ApiFixture api)
{
    private static readonly Uri BaseUri = new("https://localhost");

    private sealed record ReceivedEvent(string Type, string Payload);

    private static string Name(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..20];

    private async Task<(HttpClient Client, HubConnection Hub, ConcurrentQueue<ReceivedEvent> Events, long UserId)> ConnectAsync(params string[] roles)
    {
        var name = Name("rt");
        var id = await api.CreateUserAsync(name, roles);

        var cookies = new CookieContainerHandler();
        var client = api.CreateDefaultClient(BaseUri, cookies);
        await ApiFixture.RefreshCsrfAsync(client);
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(name, ApiFixture.TestPassword));
        Assert.True(login.IsSuccessStatusCode, await login.Content.ReadAsStringAsync());
        await ApiFixture.RefreshCsrfAsync(client);

        var events = new ConcurrentQueue<ReceivedEvent>();
        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(BaseUri, "/hubs/events"), HttpTransportType.LongPolling, options =>
            {
                options.HttpMessageHandlerFactory = _ => api.Server.CreateHandler();
                options.Headers["Cookie"] = cookies.Container.GetCookieHeader(BaseUri);
            })
            .Build();
        hub.On<ReceivedEvent>("event", events.Enqueue);
        await hub.StartAsync();
        return (client, hub, events, id);
    }

    private static HttpRequestMessage Post(string url, object body)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        message.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return message;
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, int seconds = 10)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(100);
        }

        return condition();
    }

    [Fact]
    public async Task Anonymous_connection_is_refused()
    {
        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(BaseUri, "/hubs/events"), HttpTransportType.LongPolling, options =>
                options.HttpMessageHandlerFactory = _ => api.Server.CreateHandler())
            .Build();

        await Assert.ThrowsAnyAsync<Exception>(() => hub.StartAsync());
    }

    [Fact]
    public async Task Event_reaches_allowed_departments_only_and_carries_no_patient_data()
    {
        var (reception, receptionHub, receptionEvents, _) = await ConnectAsync(Roles.Receptionist);
        var (_, triageHub, triageEvents, _) = await ConnectAsync(Roles.Triage);
        var (_, cashierHub, cashierEvents, _) = await ConnectAsync(Roles.Cashier);
        var (_, inventoryHub, inventoryEvents, _) = await ConnectAsync(Roles.Inventory);

        const string privateReason = "علت-خصوصی-مریض";
        var firstName = Name("محرمانه");
        var patientResponse = await reception.SendAsync(Post("/api/patients", new SavePatientRequest(firstName, null, null, Gender.Male, null, 30, null, null)));
        var patient = (await patientResponse.Content.ReadFromJsonAsync<PatientDto>())!;
        var departments = await reception.GetFromJsonAsync<List<LookupItem>>("/api/lookups/departments");
        var visitResponse = await reception.SendAsync(Post("/api/visits", new CreateVisitRequest(patient.Id, (int)departments![0].Id, null, privateReason)));
        var visit = (await visitResponse.Content.ReadFromJsonAsync<VisitRow>())!;

        bool Matches(ReceivedEvent e) => e.Type == "VisitCreated" && e.Payload.Contains(visit.Id.ToString());

        Assert.True(await WaitForAsync(() => triageEvents.Any(Matches) && receptionEvents.Any(Matches)), "allowed departments did not receive the event");

        // Long polling delivers per connection, so give any wrongly addressed copy time to arrive before checking.
        await Task.Delay(1000);
        Assert.DoesNotContain(cashierEvents, Matches);
        Assert.DoesNotContain(inventoryEvents, Matches);

        Assert.All(triageEvents, e =>
        {
            Assert.DoesNotContain(firstName, e.Payload);
            Assert.DoesNotContain(privateReason, e.Payload);
        });

        await receptionHub.DisposeAsync();
        await triageHub.DisposeAsync();
        await cashierHub.DisposeAsync();
        await inventoryHub.DisposeAsync();
    }

    [Fact]
    public async Task Client_cannot_join_a_group_by_calling_the_hub()
    {
        var (_, hub, _, _) = await ConnectAsync(Roles.Inventory);
        await Assert.ThrowsAnyAsync<Exception>(() => hub.InvokeAsync("AddToGroupAsync", "dept:doctor"));
        await hub.DisposeAsync();
    }

    [Fact]
    public async Task Deactivation_cuts_the_live_connection_immediately()
    {
        var adminName = Name("admin");
        await api.CreateUserAsync(adminName, Roles.SuperAdmin);
        var admin = await api.LoginAsync(adminName);

        var (_, hub, _, userId) = await ConnectAsync(Roles.Triage);
        Assert.Equal(HubConnectionState.Connected, hub.State);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/users/{userId}/deactivate", null)).StatusCode);

        Assert.True(await WaitForAsync(() => hub.State == HubConnectionState.Disconnected), "connection stayed open");
        await hub.DisposeAsync();
    }
}
