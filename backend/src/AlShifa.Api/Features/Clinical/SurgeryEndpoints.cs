using System.ComponentModel.DataAnnotations;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Finance;
using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AlShifa.Api.Features.Clinical;

public sealed record CreateSurgeryRequest(long VisitId, [Required, MaxLength(200)] string SurgeryType, EyeSide Eye, int? ServiceId = null);

public sealed record SaveSurgeryRequest(
    DateTime? ScheduledAt,
    [MaxLength(2000)] string? PreOpChecks,
    bool ConsentRecorded,
    [MaxLength(4000)] string? OperationReport,
    [MaxLength(2000)] string? PostOpStatus,
    int Version);

public sealed record ChangeSurgeryStatusRequest(SurgeryStatus Status, int Version);

public sealed record SurgeryRow(long Id, string CaseNumber, long PatientId, string PatientFileNumber, string PatientName, long VisitId, string SurgeryType, string Eye, DateTime? ScheduledAt, string Status, int Version, IReadOnlyList<string> NextStatuses);

public sealed record SurgeryDetail(SurgeryRow Case, string? PreOpChecks, bool ConsentRecorded, string? OperationReport, string? PostOpStatus);

public static class SurgeryEndpoints
{
    // Clinical gates such as required consent are intentionally not enforced until confirmed (WORKFLOWS.md M-4).
    private static readonly Dictionary<SurgeryStatus, SurgeryStatus[]> Allowed = new()
    {
        [SurgeryStatus.Proposed] = [SurgeryStatus.Scheduled, SurgeryStatus.Cancelled],
        [SurgeryStatus.Scheduled] = [SurgeryStatus.InProgress, SurgeryStatus.Cancelled],
        [SurgeryStatus.InProgress] = [SurgeryStatus.Completed],
        [SurgeryStatus.Completed] = [SurgeryStatus.FollowUp, SurgeryStatus.Closed],
        [SurgeryStatus.FollowUp] = [SurgeryStatus.Closed],
        [SurgeryStatus.Closed] = [],
        [SurgeryStatus.Cancelled] = [],
    };

    public static void MapSurgeries(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/surgeries").WithTags("Surgery");

        group.MapGet("/", async (AppDbContext db, SurgeryStatus? status, int page = 1, int pageSize = 50, CancellationToken ct = default) =>
        {
            pageSize = Math.Clamp(pageSize, 1, 100);
            page = Math.Max(page, 1);

            var query = db.SurgeryCases.AsNoTracking();
            if (status is { } s)
            {
                query = query.Where(c => c.Status == s);
            }

            var total = await query.CountAsync(ct);
            var rows = await Project(query.OrderByDescending(c => c.Id).Skip((page - 1) * pageSize).Take(pageSize)).ToListAsync(ct);
            return Results.Ok(new PagedResult<SurgeryRow>(rows.Select(ToRow).ToList(), total, page, pageSize));
        }).RequirePermission("surgery.view");

        // Clinical detail needs edit rights: viewers such as the manager only see status and schedule.
        group.MapGet("/{id:long}", async (long id, AppDbContext db, CancellationToken ct) =>
        {
            var surgery = await db.SurgeryCases.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw AppException.NotFound();
            var row = await Project(db.SurgeryCases.AsNoTracking().Where(c => c.Id == id)).SingleAsync(ct);
            return Results.Ok(new SurgeryDetail(ToRow(row), surgery.PreOpChecks, surgery.ConsentRecorded, surgery.OperationReport, surgery.PostOpStatus));
        }).RequirePermission("surgery.edit");

        group.MapPost("/", async (CreateSurgeryRequest request, AppDbContext db, Idempotency idempotency, NumberService numbers, InvoiceService invoices, CurrentUser user, AuditService audit, Outbox outbox, CancellationToken ct) =>
            await idempotency.RunAsync(request, async () =>
            {
                var patientId = await ClinicalEndpoints.PatientOfVisitAsync(db, request.VisitId, ct);
                var surgery = new SurgeryCase
                {
                    CaseNumber = $"SG-{await numbers.NextAsync("surgery", ct: ct):D6}",
                    PatientId = patientId,
                    VisitId = request.VisitId,
                    SurgeryType = request.SurgeryType.Trim(),
                    Eye = request.Eye,
                    ResponsibleUserId = user.Id,
                };
                db.SurgeryCases.Add(surgery);
                await db.SaveChangesAsync(ct);

                audit.Add("SurgeryProposed", "SurgeryCase", surgery.Id.ToString());
                outbox.Add("SurgeryScheduled", new { surgeryId = surgery.Id }, Groups.Surgery, Groups.Doctor);
                await db.SaveChangesAsync(ct);

                if (request.ServiceId is { } serviceId)
                {
                    var price = await FinanceEndpoints.CurrentPriceAsync(db, serviceId, ct);
                    await invoices.ChargeVisitAsync(patientId, request.VisitId, new ChargeLine(surgery.SurgeryType, 1, price, Accounts.ServiceRevenue, serviceId, nameof(SurgeryCase), surgery.Id), ct);
                }

                var row = await Project(db.SurgeryCases.Where(c => c.Id == surgery.Id)).SingleAsync(ct);
                return (StatusCodes.Status201Created, ToRow(row));
            }, ct)).RequirePermission("surgery.create");

        group.MapPut("/{id:long}", async (long id, SaveSurgeryRequest request, AppDbContext db, AuditService audit, CancellationToken ct) =>
        {
            var surgery = await db.SurgeryCases.SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw AppException.NotFound();
            db.Entry(surgery).Property(c => c.Version).OriginalValue = request.Version;
            if (surgery.Status is SurgeryStatus.Closed or SurgeryStatus.Cancelled)
            {
                throw new AppException(StatusCodes.Status409Conflict, "case_closed", "این پرونده بسته شده است.");
            }

            surgery.ScheduledAt = request.ScheduledAt;
            surgery.PreOpChecks = Text.Clean(request.PreOpChecks);
            surgery.ConsentRecorded = request.ConsentRecorded;
            surgery.OperationReport = Text.Clean(request.OperationReport);
            surgery.PostOpStatus = Text.Clean(request.PostOpStatus);
            audit.Add("SurgeryEdited", "SurgeryCase", id.ToString());
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequirePermission("surgery.edit");

        group.MapPost("/{id:long}/status", async (long id, ChangeSurgeryStatusRequest request, AppDbContext db, AuditService audit, Outbox outbox, HttpContext http, CancellationToken ct) =>
        {
            var needed = request.Status == SurgeryStatus.Cancelled ? "surgery.cancel" : "surgery.approve";
            if (!http.User.HasClaim(RolePermissions.ClaimType, needed))
            {
                throw new AppException(StatusCodes.Status403Forbidden, "forbidden", "شما اجازه این عملیات را ندارید.");
            }

            var surgery = await db.SurgeryCases.SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw AppException.NotFound();
            db.Entry(surgery).Property(c => c.Version).OriginalValue = request.Version;
            if (!Allowed[surgery.Status].Contains(request.Status))
            {
                throw new AppException(StatusCodes.Status409Conflict, "invalid_transition", "این تغییر وضعیت مجاز نیست.");
            }

            if (request.Status == SurgeryStatus.Scheduled && surgery.ScheduledAt is null)
            {
                throw new AppException(StatusCodes.Status409Conflict, "schedule_required", "ابتدا تاریخ عملیات را ثبت کنید.");
            }

            surgery.Status = request.Status;
            audit.Add("SurgeryStatusChanged", "SurgeryCase", id.ToString(), request.Status.ToString());
            outbox.Add("SurgeryScheduled", new { surgeryId = id }, Groups.Surgery, Groups.Doctor);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization();
    }

    private sealed record Projection(long Id, string CaseNumber, long PatientId, string FileNumber, string FirstName, string? LastName, long VisitId, string SurgeryType, EyeSide Eye, DateTime? ScheduledAt, SurgeryStatus Status, int Version);

    private static IQueryable<Projection> Project(IQueryable<SurgeryCase> cases) => cases.Select(c => new Projection(
        c.Id, c.CaseNumber, c.PatientId, c.Patient!.FileNumber, c.Patient.FirstName, c.Patient.LastName, c.VisitId, c.SurgeryType, c.Eye, c.ScheduledAt, c.Status, c.Version));

    private static SurgeryRow ToRow(Projection c) => new(
        c.Id, c.CaseNumber, c.PatientId, c.FileNumber, $"{c.FirstName} {c.LastName}".Trim(), c.VisitId, c.SurgeryType, c.Eye.ToString(), c.ScheduledAt, c.Status.ToString(), c.Version,
        Allowed[c.Status].Select(s => s.ToString()).ToList());
}
