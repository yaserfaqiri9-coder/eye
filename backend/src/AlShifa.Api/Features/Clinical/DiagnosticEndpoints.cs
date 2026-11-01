using System.ComponentModel.DataAnnotations;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Finance;
using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AlShifa.Api.Features.Clinical;

public sealed record CreateDiagnosticOrderRequest(long PatientId, long? VisitId, int TestId, [MaxLength(500)] string? Note);

public sealed record ChangeOrderStatusRequest(int Version);

public sealed record SaveResultRequest([Required, MaxLength(4000)] string ResultText, int Version);

public sealed record SaveTestRequest([Required, MaxLength(150)] string Name, bool IsActive, int? ServiceId = null);

public sealed record DiagnosticProjection(
    long Id, string OrderNumber, long PatientId, string FileNumber, string FirstName, string? LastName, long? VisitId, int TestId, string TestName,
    string? Note, DiagnosticStatus Status, DateTime CreatedAt, int Version, string? ResultText, DateTime? PerformedAt);

public sealed record DiagnosticOrderDto(
    long Id, string OrderNumber, long PatientId, string PatientFileNumber, string PatientName, long? VisitId, int TestId, string TestName,
    string? Note, string Status, DateTime CreatedAt, int Version, string? ResultText, DateTime? PerformedAt);

public static class DiagnosticEndpoints
{
    public static void MapDiagnostics(this IEndpointRouteBuilder app)
    {
        var tests = app.MapGroup("/api/diagnostic-tests").WithTags("Diagnostics");

        tests.MapGet("/", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok(await db.DiagnosticTests.AsNoTracking().OrderBy(t => t.Name).Select(t => new { t.Id, t.Name, t.IsActive }).ToListAsync(ct)))
            .RequirePermission("diagnostics.view");

        tests.MapPost("/", async (SaveTestRequest request, AppDbContext db, AuditService audit, CancellationToken ct) =>
        {
            var test = new DiagnosticTest { Name = request.Name.Trim(), IsActive = request.IsActive, ServiceId = request.ServiceId };
            db.DiagnosticTests.Add(test);
            audit.Add("DiagnosticTestCreated", "DiagnosticTest", null, test.Name);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/diagnostic-tests/{test.Id}", new { test.Id });
        }).RequirePermission("settings.edit");

        var orders = app.MapGroup("/api/diagnostic-orders").WithTags("Diagnostics");

        orders.MapGet("/", async (AppDbContext db, CurrentUser user, DiagnosticStatus? status, long? visitId, bool? mine, int page = 1, int pageSize = 50, CancellationToken ct = default) =>
        {
            pageSize = Math.Clamp(pageSize, 1, 100);
            page = Math.Max(page, 1);

            var query = db.DiagnosticOrders.AsNoTracking();
            if (mine == true)
            {
                var userId = user.Id;
                query = query.Where(o => o.RequestedByUserId == userId);
            }

            if (status is { } s)
            {
                query = query.Where(o => o.Status == s);
            }

            if (visitId is { } v)
            {
                query = query.Where(o => o.VisitId == v);
            }

            var total = await query.CountAsync(ct);
            var rows = await QueryOrders(query.OrderBy(o => o.Status).ThenByDescending(o => o.Id).Skip((page - 1) * pageSize).Take(pageSize)).ToListAsync(ct);
            return Results.Ok(new PagedResult<DiagnosticOrderDto>(rows.Select(ToDto).ToList(), total, page, pageSize));
        }).RequirePermission("diagnostics.view");

        orders.MapPost("/", async (CreateDiagnosticOrderRequest request, AppDbContext db, Idempotency idempotency, NumberService numbers, InvoiceService invoices, CurrentUser user, AuditService audit, Outbox outbox, CancellationToken ct) =>
            await idempotency.RunAsync(request, async () =>
            {
                if (!await db.Patients.AnyAsync(p => p.Id == request.PatientId, ct))
                {
                    throw new AppException(StatusCodes.Status400BadRequest, "unknown_patient", "مریض پیدا نشد.");
                }

                if (request.VisitId is { } visitId && !await db.Visits.AnyAsync(v => v.Id == visitId && v.PatientId == request.PatientId, ct))
                {
                    throw new AppException(StatusCodes.Status400BadRequest, "visit_mismatch", "این مراجعه به این مریض تعلق ندارد.");
                }

                if (!await db.DiagnosticTests.AnyAsync(t => t.Id == request.TestId && t.IsActive, ct))
                {
                    throw new AppException(StatusCodes.Status400BadRequest, "unknown_test", "آزمایش انتخاب‌شده معتبر نیست.");
                }

                var order = new DiagnosticOrder
                {
                    OrderNumber = $"LAB-{await numbers.NextAsync("lab", ct: ct):D7}",
                    PatientId = request.PatientId,
                    VisitId = request.VisitId,
                    TestId = request.TestId,
                    Note = Text.Clean(request.Note),
                    RequestedByUserId = user.Id,
                };
                db.DiagnosticOrders.Add(order);
                await db.SaveChangesAsync(ct);

                audit.Add("DiagnosticOrderCreated", "DiagnosticOrder", order.Id.ToString());
                outbox.Add("DiagnosticOrderCreated", new { orderId = order.Id }, Groups.Lab);
                await db.SaveChangesAsync(ct);

                var test = await db.DiagnosticTests.AsNoTracking().SingleAsync(t => t.Id == request.TestId, ct);
                if (test.ServiceId is { } serviceId)
                {
                    var price = await FinanceEndpoints.CurrentPriceAsync(db, serviceId, ct);
                    var line = new ChargeLine(test.Name, 1, price, Accounts.ServiceRevenue, serviceId, nameof(DiagnosticOrder), order.Id);
                    if (request.VisitId is { } chargedVisit)
                    {
                        await invoices.ChargeVisitAsync(request.PatientId, chargedVisit, line, ct);
                    }
                    else
                    {
                        await invoices.AddLineAsync(await invoices.CreateAsync(request.PatientId, null, ct), line, ct);
                    }
                }

                var row = await QueryOrders(db.DiagnosticOrders.Where(o => o.Id == order.Id)).SingleAsync(ct);
                return (StatusCodes.Status201Created, ToDto(row));
            }, ct)).RequirePermission("diagnostics.create");

        orders.MapPost("/{id:long}/start", async (long id, ChangeOrderStatusRequest request, AppDbContext db, AuditService audit, CancellationToken ct) =>
        {
            var order = await db.DiagnosticOrders.SingleOrDefaultAsync(o => o.Id == id, ct) ?? throw AppException.NotFound();
            db.Entry(order).Property(o => o.Version).OriginalValue = request.Version;
            if (order.Status != DiagnosticStatus.Requested)
            {
                throw new AppException(StatusCodes.Status409Conflict, "invalid_transition", "این درخواست قبلاً شروع شده یا بسته است.");
            }

            order.Status = DiagnosticStatus.InProgress;
            audit.Add("DiagnosticStarted", "DiagnosticOrder", id.ToString());
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequirePermission("diagnostics.edit");

        orders.MapPost("/{id:long}/result", async (long id, SaveResultRequest request, AppDbContext db, CurrentUser user, AuditService audit, Outbox outbox, CancellationToken ct) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var order = await db.DiagnosticOrders.SingleOrDefaultAsync(o => o.Id == id, ct) ?? throw AppException.NotFound();
            db.Entry(order).Property(o => o.Version).OriginalValue = request.Version;
            if (order.Status is DiagnosticStatus.ResultReady or DiagnosticStatus.Cancelled)
            {
                throw new AppException(StatusCodes.Status409Conflict, "invalid_transition", "برای این درخواست نتیجه ثبت شده یا لغو شده است.");
            }

            db.DiagnosticResults.Add(new DiagnosticResult { OrderId = id, ResultText = request.ResultText.Trim(), PerformedByUserId = user.Id });
            order.Status = DiagnosticStatus.ResultReady;
            audit.Add("DiagnosticResultSaved", "DiagnosticOrder", id.ToString());
            outbox.Add("DiagnosticResultReady", new { orderId = id, visitId = order.VisitId }, Groups.Doctor, Groups.Lab);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        }).RequirePermission("diagnostics.approve");

        orders.MapPost("/{id:long}/cancel", async (long id, ChangeOrderStatusRequest request, AppDbContext db, InvoiceService invoices, AuditService audit, CancellationToken ct) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var order = await db.DiagnosticOrders.SingleOrDefaultAsync(o => o.Id == id, ct) ?? throw AppException.NotFound();
            db.Entry(order).Property(o => o.Version).OriginalValue = request.Version;
            if (order.Status is DiagnosticStatus.ResultReady or DiagnosticStatus.Cancelled)
            {
                throw new AppException(StatusCodes.Status409Conflict, "invalid_transition", "این درخواست قابل لغو نیست.");
            }

            var charge = await db.InvoiceItems.SingleOrDefaultAsync(i => i.SourceType == nameof(DiagnosticOrder) && i.SourceId == id && !i.IsVoid, ct);
            if (charge is not null)
            {
                await invoices.VoidLineAsync(charge, ct);
            }

            order.Status = DiagnosticStatus.Cancelled;
            audit.Add("DiagnosticCancelled", "DiagnosticOrder", id.ToString());
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        }).RequirePermission("diagnostics.cancel");
    }

    public static IQueryable<DiagnosticProjection> QueryOrders(IQueryable<DiagnosticOrder> orders) =>
        orders.Select(o => new DiagnosticProjection(
            o.Id, o.OrderNumber, o.PatientId, o.Patient!.FileNumber, o.Patient.FirstName, o.Patient.LastName, o.VisitId, o.TestId, o.Test!.Name,
            o.Note, o.Status, o.CreatedAt, o.Version, o.Result != null ? o.Result.ResultText : null, o.Result != null ? (DateTime?)o.Result.PerformedAt : null));

    public static DiagnosticOrderDto ToDto(DiagnosticProjection o) => new(
        o.Id, o.OrderNumber, o.PatientId, o.FileNumber, $"{o.FirstName} {o.LastName}".Trim(), o.VisitId, o.TestId, o.TestName,
        o.Note, o.Status.ToString(), o.CreatedAt, o.Version, o.ResultText, o.PerformedAt);
}
