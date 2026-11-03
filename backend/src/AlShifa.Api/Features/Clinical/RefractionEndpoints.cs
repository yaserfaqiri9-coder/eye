using System.ComponentModel.DataAnnotations;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AlShifa.Api.Features.Clinical;

public sealed record RefractionLineInput(
    RefractionStage Stage,
    EyeSide Eye,
    [MaxLength(10)] string? Sphere,
    [MaxLength(10)] string? Cylinder,
    [MaxLength(10)] string? Axis,
    [MaxLength(10)] string? Add,
    [MaxLength(30)] string? Va);

public sealed record SaveRefractionRequest(
    [MaxLength(30)] string? UnaidedVaOd,
    [MaxLength(30)] string? UnaidedVaOs,
    [MaxLength(30)] string? PinholeVaOd,
    [MaxLength(30)] string? PinholeVaOs,
    [MaxLength(30)] string? NearVaOd,
    [MaxLength(30)] string? NearVaOs,
    [MaxLength(10)] string? Pd,
    [MaxLength(1000)] string? Measurements,
    [MaxLength(2000)] string? Notes,
    RefractionLineInput[]? Lines,
    int? Version);

public sealed record RefractionLineDto(string Stage, string Eye, string? Sphere, string? Cylinder, string? Axis, string? Add, string? Va);

public sealed record RefractionDto(
    long Id, long VisitId, DateOnly VisitDate, string? UnaidedVaOd, string? UnaidedVaOs, string? PinholeVaOd, string? PinholeVaOs,
    string? NearVaOd, string? NearVaOs, string? Pd, string? Measurements, string? Notes, DateTime RecordedAt, DateTime? CompletedAt, int Version,
    IReadOnlyList<RefractionLineDto> Lines);

/// <summary>
/// Recorded under the pre-exam permissions. It starts only from the doctor's paid request and, once complete, returns
/// the patient to the doctor. Values are stored exactly as entered. Fields are provisional until the medical lead
/// confirms them (WORKFLOWS.md M-8).
/// </summary>
public static class RefractionEndpoints
{
    public static void MapRefractions(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/refractions").WithTags("Refraction");

        group.MapGet("/{visitId:long}", async (long visitId, AppDbContext db, ClinicalAccess access, CancellationToken ct) =>
        {
            var patientId = await ClinicalEndpoints.PatientOfVisitAsync(db, visitId, ct);
            await access.EnsureCanViewAsync(patientId, ct);
            var row = await Query(db).SingleOrDefaultAsync(r => r.VisitId == visitId, ct);
            return row is null ? Results.NoContent() : Results.Ok(ToDto(row));
        }).RequirePermission("preexam.view");

        group.MapGet("/queue", async (AppDbContext db, CancellationToken ct) =>
        {
            var today = KabulClock.Today;
            var rows = await db.DiagnosticOrders.AsNoTracking()
                .Where(o => o.Test!.Kind == DiagnosticKind.Refraction && o.Visit!.VisitDate == today && o.Visit.Status != Visits.VisitStatus.Cancelled
                    && (o.Status == DiagnosticStatus.Requested || o.Status == DiagnosticStatus.InProgress))
                .OrderBy(o => o.Visit!.QueueNumber)
                .Take(200)
                .Select(o => new
                {
                    o.Id,
                    VisitId = o.VisitId!.Value,
                    o.Visit!.QueueNumber,
                    o.Patient!.FileNumber,
                    o.Patient.FirstName,
                    o.Patient.LastName,
                    Status = o.Status.ToString(),
                    Paid = o.Invoice != null && o.Invoice.Status != Finance.InvoiceStatus.Void && o.Invoice.Total - o.Invoice.Discount <= o.Invoice.Paid - o.Invoice.Refunded,
                })
                .ToListAsync(ct);
            return Results.Ok(rows.Select(r => new { r.Id, r.VisitId, r.QueueNumber, PatientFileNumber = r.FileNumber, PatientName = $"{r.FirstName} {r.LastName}".Trim(), r.Status, r.Paid }));
        }).RequirePermission("preexam.view");

        group.MapPut("/{visitId:long}", async (long visitId, SaveRefractionRequest request, AppDbContext db, CurrentUser user, AuditService audit, CancellationToken ct) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            ClinicalEndpoints.EnsureNotCancelled(await Visits.VisitService.LockAsync(db, visitId, ct));
            var row = await db.Refractions.SingleOrDefaultAsync(r => r.VisitId == visitId, ct);
            if (row is null)
            {
                row = new Refraction { VisitId = visitId, RecordedByUserId = user.Id };
                db.Refractions.Add(row);
            }
            else
            {
                // A screen with no version opened before the first save, so it never saw what is stored now.
                db.Entry(row).Property(r => r.Version).OriginalValue = request.Version ?? throw new DbUpdateConcurrencyException();
                if (row.CompletedAt is not null)
                {
                    throw new AppException(StatusCodes.Status409Conflict, "already_completed", "این بینایی‌سنجی تکمیل شده و قابل ویرایش نیست.");
                }

                // A save that changes only the lines must still pass the version check and move the version on.
                db.Entry(row).State = EntityState.Modified;
            }

            var order = await OpenOrderAsync(db, visitId, ct);
            if (order.Status == DiagnosticStatus.Requested)
            {
                await DiagnosticGate.EnsurePaidAsync(db, order, ct);
                order.Status = DiagnosticStatus.InProgress;
                audit.Add("DiagnosticStarted", "DiagnosticOrder", order.Id.ToString());
            }

            row.UnaidedVaOd = Text.Clean(request.UnaidedVaOd);
            row.UnaidedVaOs = Text.Clean(request.UnaidedVaOs);
            row.PinholeVaOd = Text.Clean(request.PinholeVaOd);
            row.PinholeVaOs = Text.Clean(request.PinholeVaOs);
            row.NearVaOd = Text.Clean(request.NearVaOd);
            row.NearVaOs = Text.Clean(request.NearVaOs);
            row.Pd = Text.Clean(request.Pd);
            row.Measurements = Text.Clean(request.Measurements);
            row.Notes = Text.Clean(request.Notes);

            var lines = (request.Lines ?? [])
                .Where(l => l.Eye is EyeSide.OD or EyeSide.OS)
                .Select(l => new RefractionLine
                {
                    Stage = l.Stage,
                    Eye = l.Eye,
                    Sphere = Text.Clean(l.Sphere),
                    Cylinder = Text.Clean(l.Cylinder),
                    Axis = Text.Clean(l.Axis),
                    Add = Text.Clean(l.Add),
                    Va = Text.Clean(l.Va),
                })
                .Where(l => l.Sphere is not null || l.Cylinder is not null || l.Axis is not null || l.Add is not null || l.Va is not null)
                .ToList();

            if (lines.DistinctBy(l => (l.Stage, l.Eye)).Count() != lines.Count)
            {
                throw new AppException(StatusCodes.Status400BadRequest, "duplicate_line", "برای هر مرحله و هر چشم فقط یک ردیف مجاز است.");
            }

            audit.Add("RefractionSaved", "Visit", visitId.ToString());
            await db.SaveChangesAsync(ct);

            await db.RefractionLines.Where(l => l.RefractionId == row.Id).ExecuteDeleteAsync(ct);
            foreach (var line in lines)
            {
                line.RefractionId = row.Id;
            }

            db.RefractionLines.AddRange(lines);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return Results.Ok(ToDto(await Query(db).SingleAsync(r => r.Id == row.Id, ct)));
        }).RequirePermission("preexam.edit");

        group.MapPost("/{visitId:long}/complete", async (long visitId, AppDbContext db, CurrentUser user, AuditService audit, Outbox outbox, CancellationToken ct) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await Visits.VisitService.LockAsync(db, visitId, ct);
            var row = await db.Refractions.SingleOrDefaultAsync(r => r.VisitId == visitId, ct)
                ?? throw new AppException(StatusCodes.Status409Conflict, "nothing_to_complete", "ابتدا بینایی‌سنجی را ذخیره کنید.");
            if (row.CompletedAt is not null)
            {
                return Results.NoContent();
            }

            var order = await OpenOrderAsync(db, visitId, ct);
            row.CompletedAt = DateTime.UtcNow;
            order.Status = DiagnosticStatus.ResultReady;
            db.DiagnosticResults.Add(new DiagnosticResult { OrderId = order.Id, ResultText = "نتیجه در فرم بینایی‌سنجی ثبت شد.", PerformedByUserId = user.Id });
            audit.Add("RefractionCompleted", "Visit", visitId.ToString());
            outbox.Add("DiagnosticResultReady", new { orderId = order.Id, visitId }, Groups.Doctor, Groups.Triage);
            await db.SaveChangesAsync(ct);
            await DiagnosticGate.ReturnToDoctorAsync(db, visitId, user, outbox, ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        }).RequirePermission("preexam.edit");

        app.MapGet("/api/clinical/patients/{patientId:long}/refractions", async (long patientId, AppDbContext db, ClinicalAccess access, CancellationToken ct) =>
        {
            await access.EnsureCanViewAsync(patientId, ct);
            var rows = await Query(db).Where(r => r.Visit!.PatientId == patientId).OrderByDescending(r => r.Id).Take(20).ToListAsync(ct);
            return Results.Ok(rows.Select(ToDto).ToList());
        }).RequirePermission("preexam.view").WithTags("Refraction");
    }

    private static async Task<DiagnosticOrder> OpenOrderAsync(AppDbContext db, long visitId, CancellationToken ct) =>
        await db.DiagnosticOrders.FirstOrDefaultAsync(o => o.VisitId == visitId && o.Test!.Kind == DiagnosticKind.Refraction
            && (o.Status == DiagnosticStatus.Requested || o.Status == DiagnosticStatus.InProgress), ct)
        ?? throw new AppException(StatusCodes.Status409Conflict, "refraction_not_requested", "رفرکشن فقط با درخواست داکتر شروع می‌شود.");

    private static IQueryable<Refraction> Query(AppDbContext db) =>
        db.Refractions.AsNoTracking().Include(r => r.Visit).Include(r => r.Lines);

    private static RefractionDto ToDto(Refraction r) => new(
        r.Id, r.VisitId, r.Visit!.VisitDate, r.UnaidedVaOd, r.UnaidedVaOs, r.PinholeVaOd, r.PinholeVaOs, r.NearVaOd, r.NearVaOs, r.Pd, r.Measurements, r.Notes,
        r.RecordedAt, r.CompletedAt, r.Version,
        r.Lines.OrderBy(l => l.Stage).ThenBy(l => l.Eye).Select(l => new RefractionLineDto(l.Stage.ToString(), l.Eye.ToString(), l.Sphere, l.Cylinder, l.Axis, l.Add, l.Va)).ToList());
}
