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
    int? Version,
    [MaxLength(500)] string? AmendReason = null);

public sealed record RefractionLineDto(string Stage, string Eye, string? Sphere, string? Cylinder, string? Axis, string? Add, string? Va);

public sealed record RefractionDto(
    long Id, long VisitId, DateOnly VisitDate, string? UnaidedVaOd, string? UnaidedVaOs, string? PinholeVaOd, string? PinholeVaOs,
    string? NearVaOd, string? NearVaOs, string? Pd, string? Measurements, string? Notes, DateTime RecordedAt, DateTime? CompletedAt, int Version,
    IReadOnlyList<RefractionLineDto> Lines, string? OpticalRxNumber = null);

/// <param name="ReturnToDoctor">The patient goes back to the requesting doctor's queue. Without it the patient leaves for the optical desk only.</param>
/// <param name="SendToOptical">The final values and PD become an optical prescription the optical desk sees at once.</param>
public sealed record CompleteRefractionRequest(bool ReturnToDoctor = true, bool SendToOptical = false);

/// <summary>
/// Recorded under the pre-exam permissions. It starts only from the doctor's paid request and, once complete, returns
/// the patient to the doctor. Values are stored exactly as entered. Fields are provisional until the medical lead
/// confirms them (WORKFLOWS.md M-8). The refractionist may send the final values to the optical desk as an optical
/// prescription; a correction to the refraction then follows into it until glasses are ordered against it.
/// </summary>
public static class RefractionEndpoints
{
    /// <summary>The filled rows in a fixed order, so the same values always read the same however they arrived.</summary>
    private static string DescribeLines(IEnumerable<(RefractionStage Stage, EyeSide Eye, string? Sphere, string? Cylinder, string? Axis, string? Add, string? Va)> lines) =>
        string.Join("\n", lines
            .Where(l => l.Sphere is not null || l.Cylinder is not null || l.Axis is not null || l.Add is not null || l.Va is not null)
            .OrderBy(l => l.Stage).ThenBy(l => l.Eye)
            .Select(l => $"{l.Stage} {l.Eye}: {l.Sphere ?? "-"} / {l.Cylinder ?? "-"} x {l.Axis ?? "-"} ADD {l.Add ?? "-"} VA {l.Va ?? "-"}"));

    public static void MapRefractions(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/refractions").WithTags("Refraction");

        group.MapGet("/{visitId:long}", async (long visitId, AppDbContext db, ClinicalAccess access, CancellationToken ct) =>
        {
            var patientId = await ClinicalEndpoints.PatientOfVisitAsync(db, visitId, ct);
            await access.EnsureCanViewAsync(patientId, ct);
            var row = await Query(db).SingleOrDefaultAsync(r => r.VisitId == visitId, ct);
            return row is null ? Results.NoContent() : Results.Ok(await ToDtoAsync(db, row, ct));
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

        group.MapPut("/{visitId:long}", async (long visitId, SaveRefractionRequest request, AppDbContext db, CurrentUser user, AuditService audit, Outbox outbox, CancellationToken ct) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            ClinicalEndpoints.EnsureNotCancelled(await Visits.VisitService.LockAsync(db, visitId, ct));
            var row = await db.Refractions.SingleOrDefaultAsync(r => r.VisitId == visitId, ct);
            string? amendReason = null;
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
                    // The doctor and the optical desk may already have used these numbers, so what changed and why stays on the record.
                    amendReason = Text.Clean(request.AmendReason)
                        ?? throw new AppException(StatusCodes.Status409Conflict, "already_completed", "این بینایی‌سنجی تکمیل شده است. برای تغییر، دلیل اصلاح را بنویسید.");
                }

                // A save that changes only the lines must still pass the version check and move the version on.
                db.Entry(row).State = EntityState.Modified;
            }

            // A completed refraction has finished its request; correcting it does not open anything again.
            if (amendReason is null)
            {
                var order = await OpenOrderAsync(db, visitId, ct);
                if (order.Status == DiagnosticStatus.Requested)
                {
                    await DiagnosticGate.EnsurePaidAsync(db, order, ct);
                    order.Status = DiagnosticStatus.InProgress;
                    audit.Add("DiagnosticStarted", "DiagnosticOrder", order.Id.ToString());
                }
            }
            else
            {
                var oldLines = await db.RefractionLines.AsNoTracking().Where(l => l.RefractionId == row.Id).ToListAsync(ct);
                Amendments.AddChanges(db, nameof(Refraction), row.Id,
                [
                    (nameof(row.UnaidedVaOd), row.UnaidedVaOd, Text.Clean(request.UnaidedVaOd)),
                    (nameof(row.UnaidedVaOs), row.UnaidedVaOs, Text.Clean(request.UnaidedVaOs)),
                    (nameof(row.PinholeVaOd), row.PinholeVaOd, Text.Clean(request.PinholeVaOd)),
                    (nameof(row.PinholeVaOs), row.PinholeVaOs, Text.Clean(request.PinholeVaOs)),
                    (nameof(row.NearVaOd), row.NearVaOd, Text.Clean(request.NearVaOd)),
                    (nameof(row.NearVaOs), row.NearVaOs, Text.Clean(request.NearVaOs)),
                    (nameof(row.Pd), row.Pd, Text.Clean(request.Pd)),
                    (nameof(row.Measurements), row.Measurements, Text.Clean(request.Measurements)),
                    (nameof(row.Notes), row.Notes, Text.Clean(request.Notes)),
                    (nameof(row.Lines), DescribeLines(oldLines.Select(l => (l.Stage, l.Eye, l.Sphere, l.Cylinder, l.Axis, l.Add, l.Va))),
                        DescribeLines((request.Lines ?? []).Select(l => (l.Stage, l.Eye, Text.Clean(l.Sphere), Text.Clean(l.Cylinder), Text.Clean(l.Axis), Text.Clean(l.Add), Text.Clean(l.Va))))),
                ], amendReason, user.Id);
                audit.Add("RefractionAmended", "Visit", visitId.ToString(), amendReason);
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

            await db.ExecuteDeleteSyncedAsync(db.RefractionLines.Where(l => l.RefractionId == row.Id), ct);
            foreach (var line in lines)
            {
                line.RefractionId = row.Id;
            }

            db.RefractionLines.AddRange(lines);
            if (amendReason is not null)
            {
                await FollowCorrectionAsync(db, row, lines, amendReason, user, audit, outbox, ct);
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return Results.Ok(await ToDtoAsync(db, await Query(db).SingleAsync(r => r.Id == row.Id, ct), ct));
        }).RequirePermission("preexam.edit");

        group.MapPost("/{visitId:long}/complete", async (long visitId, CompleteRefractionRequest? request, AppDbContext db, NumberService numbers, CurrentUser user, AuditService audit, Outbox outbox, CancellationToken ct) =>
        {
            request ??= new CompleteRefractionRequest();
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
            audit.Add("RefractionCompleted", "Visit", visitId.ToString(), request.ReturnToDoctor ? null : "Not returned to the doctor");
            outbox.Add("DiagnosticResultReady", new { orderId = order.Id, visitId }, Groups.Doctor, Groups.Triage);
            await db.SaveChangesAsync(ct);
            if (request.SendToOptical)
            {
                await SendToOpticalAsync(db, row, numbers, user, audit, outbox, ct);
            }

            if (request.ReturnToDoctor)
            {
                await DiagnosticGate.ReturnToDoctorAsync(db, visitId, user, outbox, ct);
            }

            await tx.CommitAsync(ct);
            return Results.NoContent();
        }).RequirePermission("preexam.edit");

        // For a refraction completed without sending, or whose copy at the optical desk was cancelled.
        group.MapPost("/{visitId:long}/send-to-optical", async (long visitId, AppDbContext db, NumberService numbers, CurrentUser user, AuditService audit, Outbox outbox, CancellationToken ct) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            ClinicalEndpoints.EnsureNotCancelled(await Visits.VisitService.LockAsync(db, visitId, ct));
            var row = await db.Refractions.SingleOrDefaultAsync(r => r.VisitId == visitId, ct);
            if (row?.CompletedAt is null)
            {
                throw new AppException(StatusCodes.Status409Conflict, "not_completed", "ابتدا بینایی‌سنجی را تکمیل کنید.");
            }

            await SendToOpticalAsync(db, row, numbers, user, audit, outbox, ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        }).RequirePermission("preexam.edit");

        app.MapGet("/api/clinical/patients/{patientId:long}/refractions", async (long patientId, AppDbContext db, ClinicalAccess access, CancellationToken ct) =>
        {
            await access.EnsureCanViewAsync(patientId, ct);
            var rows = await Query(db).Where(r => r.Visit!.PatientId == patientId).OrderByDescending(r => r.Id).Take(20).ToListAsync(ct);
            var ids = rows.Select(r => r.Id).ToList();
            var sent = await ActiveCopies(db).Where(p => ids.Contains(p.RefractionId!.Value))
                .Select(p => new { RefractionId = p.RefractionId!.Value, p.RxNumber }).ToListAsync(ct);
            return Results.Ok(rows.Select(r => ToDto(r, sent.FirstOrDefault(s => s.RefractionId == r.Id)?.RxNumber)).ToList());
        }).RequirePermission("preexam.view").WithTags("Refraction");
    }

    private static async Task<DiagnosticOrder> OpenOrderAsync(AppDbContext db, long visitId, CancellationToken ct) =>
        await db.DiagnosticOrders.FirstOrDefaultAsync(o => o.VisitId == visitId && o.Test!.Kind == DiagnosticKind.Refraction
            && (o.Status == DiagnosticStatus.Requested || o.Status == DiagnosticStatus.InProgress), ct)
        ?? throw new AppException(StatusCodes.Status409Conflict, "refraction_not_requested", "رفرکشن فقط با درخواست داکتر شروع می‌شود.");

    private static IQueryable<OpticalPrescription> ActiveCopies(AppDbContext db) =>
        db.OpticalPrescriptions.Where(p => p.RefractionId != null && !p.IsCancelled);

    private static (string? Sphere, string? Cylinder, string? Axis, string? Add) FinalOf(IEnumerable<RefractionLine> lines, EyeSide eye) =>
        lines.SingleOrDefault(l => l.Stage == RefractionStage.Final && l.Eye == eye) is { } l ? (l.Sphere, l.Cylinder, l.Axis, l.Add) : default;

    /// <summary>Copies the final row of each eye and the PD into an optical prescription. The caller holds the visit lock.</summary>
    private static async Task SendToOpticalAsync(AppDbContext db, Refraction row, NumberService numbers, CurrentUser user, AuditService audit, Outbox outbox, CancellationToken ct)
    {
        if (await ActiveCopies(db).AnyAsync(p => p.RefractionId == row.Id, ct))
        {
            throw new AppException(StatusCodes.Status409Conflict, "already_sent", "نتیجه این بینایی‌سنجی قبلاً به بخش عینک فرستاده شده است.");
        }

        var lines = await db.RefractionLines.AsNoTracking().Where(l => l.RefractionId == row.Id && l.Stage == RefractionStage.Final).ToListAsync(ct);
        var od = FinalOf(lines, EyeSide.OD);
        var os = FinalOf(lines, EyeSide.OS);
        if (od is (null, null, null, null) && os is (null, null, null, null))
        {
            throw new AppException(StatusCodes.Status409Conflict, "no_final_values", "برای ارسال به بخش عینک، ردیف «نهایی» (Final) را پر و ذخیره کنید.");
        }

        var rx = new OpticalPrescription
        {
            RxNumber = $"OP-{await numbers.NextAsync("optical-rx", ct: ct):D7}",
            PatientId = await db.Visits.Where(v => v.Id == row.VisitId).Select(v => v.PatientId).SingleAsync(ct),
            VisitId = row.VisitId,
            DoctorUserId = user.Id,
            RefractionId = row.Id,
            SphereOd = od.Sphere,
            CylinderOd = od.Cylinder,
            AxisOd = od.Axis,
            AddOd = od.Add,
            SphereOs = os.Sphere,
            CylinderOs = os.Cylinder,
            AxisOs = os.Axis,
            AddOs = os.Add,
            Pd = row.Pd,
            Notes = "از بینایی‌سنجی",
        };
        db.OpticalPrescriptions.Add(rx);
        await db.SaveChangesAsync(ct);

        audit.Add("RefractionSentToOptical", "OpticalPrescription", rx.Id.ToString(), $"Visit {row.VisitId}");
        outbox.Add("OpticalPrescriptionCreated", new { prescriptionId = rx.Id }, Groups.Optical, Groups.Doctor);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// A correction reaches the optical desk's copy while no glasses are ordered on it. Once lenses may be in the
    /// workshop the copy stays as ordered, the rule the doctor's own optical prescriptions follow.
    /// </summary>
    private static async Task FollowCorrectionAsync(AppDbContext db, Refraction row, List<RefractionLine> lines, string reason, CurrentUser user, AuditService audit, Outbox outbox, CancellationToken ct)
    {
        var rx = await ActiveCopies(db).SingleOrDefaultAsync(p => p.RefractionId == row.Id, ct);
        if (rx is null)
        {
            return;
        }

        if (await db.OpticalOrders.AnyAsync(o => o.OpticalPrescriptionId == rx.Id && o.Status != Inventory.OpticalOrderStatus.Cancelled, ct))
        {
            audit.Add("RefractionAmendedAfterOpticalOrder", "OpticalPrescription", rx.Id.ToString(), reason);
            return;
        }

        var od = FinalOf(lines, EyeSide.OD);
        var os = FinalOf(lines, EyeSide.OS);
        Amendments.AddChanges(db, nameof(OpticalPrescription), rx.Id,
        [
            (nameof(rx.SphereOd), rx.SphereOd, od.Sphere),
            (nameof(rx.CylinderOd), rx.CylinderOd, od.Cylinder),
            (nameof(rx.AxisOd), rx.AxisOd, od.Axis),
            (nameof(rx.AddOd), rx.AddOd, od.Add),
            (nameof(rx.SphereOs), rx.SphereOs, os.Sphere),
            (nameof(rx.CylinderOs), rx.CylinderOs, os.Cylinder),
            (nameof(rx.AxisOs), rx.AxisOs, os.Axis),
            (nameof(rx.AddOs), rx.AddOs, os.Add),
            (nameof(rx.Pd), rx.Pd, row.Pd),
        ], reason, user.Id);

        (rx.SphereOd, rx.CylinderOd, rx.AxisOd, rx.AddOd) = od;
        (rx.SphereOs, rx.CylinderOs, rx.AxisOs, rx.AddOs) = os;
        rx.Pd = row.Pd;
        audit.Add("OpticalPrescriptionEdited", "OpticalPrescription", rx.Id.ToString(), reason);
        outbox.Add("OpticalPrescriptionCreated", new { prescriptionId = rx.Id }, Groups.Optical, Groups.Doctor);
    }

    private static async Task<RefractionDto> ToDtoAsync(AppDbContext db, Refraction r, CancellationToken ct) =>
        ToDto(r, await ActiveCopies(db).Where(p => p.RefractionId == r.Id).Select(p => p.RxNumber).FirstOrDefaultAsync(ct));

    private static IQueryable<Refraction> Query(AppDbContext db) =>
        db.Refractions.AsNoTracking().Include(r => r.Visit).Include(r => r.Lines);

    private static RefractionDto ToDto(Refraction r, string? opticalRxNumber) => new(
        r.Id, r.VisitId, r.Visit!.VisitDate, r.UnaidedVaOd, r.UnaidedVaOs, r.PinholeVaOd, r.PinholeVaOs, r.NearVaOd, r.NearVaOs, r.Pd, r.Measurements, r.Notes,
        r.RecordedAt, r.CompletedAt, r.Version,
        r.Lines.OrderBy(l => l.Stage).ThenBy(l => l.Eye).Select(l => new RefractionLineDto(l.Stage.ToString(), l.Eye.ToString(), l.Sphere, l.Cylinder, l.Axis, l.Add, l.Va)).ToList(),
        opticalRxNumber);
}
