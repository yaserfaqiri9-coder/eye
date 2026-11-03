using System.Collections.Concurrent;
using System.Security.Claims;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Dept = AlShifa.Api.Infrastructure.Groups;

namespace AlShifa.Api.Features.Realtime;

/// <summary>Live connections per user, so access can be cut the moment an account changes.</summary>
public sealed class ConnectionTracker
{
    private readonly ConcurrentDictionary<string, HubCallerContext> connections = new();

    public void Add(HubCallerContext context) => connections[context.ConnectionId] = context;

    public void Remove(string connectionId) => connections.TryRemove(connectionId, out _);

    public void Disconnect(long userId)
    {
        var id = userId.ToString();
        foreach (var context in connections.Values.Where(c => c.UserIdentifier == id))
        {
            context.Abort();
        }
    }
}

[Authorize]
public sealed class EventsHub(ConnectionTracker tracker) : Hub
{
    public const string Path = "/hubs/events";

    /// <summary>Groups come only from the server-side permissions; clients have no method to join one.</summary>
    private static readonly (string Group, string Permission)[] Membership =
    [
        (Dept.Reception, "visits.create"),
        (Dept.Triage, "preexam.edit"),
        (Dept.Doctor, "consultation.edit"),
        (Dept.Lab, "diagnostics.edit"),
        (Dept.Surgery, "surgery.edit"),
        (Dept.Pharmacy, "pharmacy.create"),
        (Dept.Optical, "optical.create"),
        (Dept.Cashier, "payments.create"),
        (Dept.Finance, "ledger.view"),
        (Dept.Inventory, "inventory.approve"),
        (Dept.Management, "reports.print"),
        (Dept.Audio, "calls.manage"),
    ];

    public override async Task OnConnectedAsync()
    {
        tracker.Add(Context);
        foreach (var (group, permission) in Membership)
        {
            if (Context.User!.HasClaim(RolePermissions.ClaimType, permission))
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, group);
            }
        }

        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        tracker.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}

public sealed class UserIdFromNameIdentifier : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection) => connection.User?.FindFirstValue(ClaimTypes.NameIdentifier);
}

/// <summary>Sends committed outbox rows to SignalR. A send failure leaves the row for the next pass.</summary>
public sealed class OutboxDispatcher(IServiceScopeFactory scopes, IHubContext<EventsHub> hub, ILogger<OutboxDispatcher> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastCleanup = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await DispatchAsync(db, stoppingToken);

                if (DateTime.UtcNow - lastCleanup > TimeSpan.FromHours(1))
                {
                    var cutoff = DateTime.UtcNow - Retention;
                    await db.OutboxEvents.Where(e => e.ProcessedAt != null && e.ProcessedAt < cutoff).ExecuteDeleteAsync(stoppingToken);
                    await db.IdempotencyRecords.Where(r => r.CreatedAt < cutoff).ExecuteDeleteAsync(stoppingToken);
                    lastCleanup = DateTime.UtcNow;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // A database outage must not stop the loop; events stay queued until it recovers.
                logger.LogWarning(e, "Outbox dispatch failed");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task DispatchAsync(AppDbContext db, CancellationToken ct)
    {
        var batch = await db.OutboxEvents.Where(e => e.ProcessedAt == null).OrderBy(e => e.Id).Take(100).ToListAsync(ct);
        foreach (var e in batch)
        {
            var groups = e.Groups.Split(',', StringSplitOptions.RemoveEmptyEntries);
            await hub.Clients.Groups(groups).SendAsync("event", new { type = e.Type, payload = e.Payload }, ct);
            e.ProcessedAt = DateTime.UtcNow;
        }

        if (batch.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }
    }
}
