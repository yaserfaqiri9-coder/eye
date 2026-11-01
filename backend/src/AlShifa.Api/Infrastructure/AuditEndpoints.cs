using AlShifa.Api.Features.Auth;
using Microsoft.EntityFrameworkCore;

namespace AlShifa.Api.Infrastructure;

public sealed record AuditRow(long Id, string? UserName, string Action, string Entity, string? EntityId, string? Details, string? IpAddress, DateTime CreatedAt);

public static class AuditEndpoints
{
    public static void MapAudit(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/audit", async (AppDbContext db, string? action, int page = 1, int pageSize = 50, CancellationToken ct = default) =>
        {
            pageSize = Math.Clamp(pageSize, 1, 100);
            page = Math.Max(page, 1);

            var query = db.AuditLogs.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(action))
            {
                query = query.Where(a => a.Action == action);
            }

            var total = await query.CountAsync(ct);
            var rows = await (
                from a in query.OrderByDescending(a => a.Id).Skip((page - 1) * pageSize).Take(pageSize)
                join u in db.Users on a.UserId equals u.Id into users
                from user in users.DefaultIfEmpty()
                orderby a.Id descending
                select new AuditRow(a.Id, user != null ? user.FullName : null, a.Action, a.Entity, a.EntityId, a.Details, a.IpAddress, a.CreatedAt))
                .ToListAsync(ct);

            return Results.Ok(new PagedResult<AuditRow>(rows, total, page, pageSize));
        }).RequirePermission("audit.view").WithTags("Audit");
    }
}
