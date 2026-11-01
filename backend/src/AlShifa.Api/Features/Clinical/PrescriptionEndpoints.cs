using AlShifa.Api.Features.Auth;
using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AlShifa.Api.Features.Clinical;

public static class PrescriptionEndpoints
{
    public static void MapPrescriptions(this IEndpointRouteBuilder app)
    {
        var medical = app.MapGroup("/api/prescriptions").WithTags("Prescriptions");

        medical.MapGet("/", async (AppDbContext db, PrescriptionStatus? status, string? q, CancellationToken ct) =>
        {
            var query = db.MedicalPrescriptions.AsNoTracking();
            if (status is { } s)
            {
                query = query.Where(p => p.Status == s);
            }

            if (!string.IsNullOrWhiteSpace(q))
            {
                query = query.Where(p => p.RxNumber.StartsWith(q) || p.Patient!.FileNumber.StartsWith(q) || p.Patient.FirstName.StartsWith(q));
            }

            return Results.Ok(await QueryAsync(db, query.OrderByDescending(p => p.Id).Take(100), ct));
        }).RequirePermission("prescriptions.view");

        medical.MapPost("/", async (CreatePrescriptionRequest request, AppDbContext db, Idempotency idempotency, NumberService numbers, CurrentUser user, AuditService audit, Outbox outbox, CancellationToken ct) =>
            await idempotency.RunAsync(request, async () =>
            {
                var patientId = await ClinicalEndpoints.PatientOfVisitAsync(db, request.VisitId, ct);
                var rx = new MedicalPrescription
                {
                    RxNumber = $"RX-{await numbers.NextAsync("rx", ct: ct):D7}",
                    PatientId = patientId,
                    VisitId = request.VisitId,
                    DoctorUserId = user.Id,
                    Notes = Text.Clean(request.Notes),
                    Items = request.Items.Select(i => new MedicalPrescriptionItem
                    {
                        DrugName = i.DrugName.Trim(),
                        Dose = Text.Clean(i.Dose),
                        Route = Text.Clean(i.Route),
                        Duration = Text.Clean(i.Duration),
                        Instructions = Text.Clean(i.Instructions),
                        Quantity = i.Quantity,
                    }).ToList(),
                };
                db.MedicalPrescriptions.Add(rx);
                await db.SaveChangesAsync(ct);

                audit.Add("PrescriptionCreated", "MedicalPrescription", rx.Id.ToString());
                outbox.Add("PrescriptionCreated", new { prescriptionId = rx.Id }, Groups.Pharmacy, Groups.Doctor);
                await db.SaveChangesAsync(ct);

                var dto = await QueryAsync(db, db.MedicalPrescriptions.Where(p => p.Id == rx.Id), ct);
                return (StatusCodes.Status201Created, dto.Single());
            }, ct)).RequirePermission("prescriptions.create");

        medical.MapPost("/{id:long}/cancel", async (long id, int version, AppDbContext db, AuditService audit, CancellationToken ct) =>
        {
            var rx = await db.MedicalPrescriptions.SingleOrDefaultAsync(p => p.Id == id, ct) ?? throw AppException.NotFound();
            db.Entry(rx).Property(p => p.Version).OriginalValue = version;
            if (rx.Status != PrescriptionStatus.Issued)
            {
                throw new AppException(StatusCodes.Status409Conflict, "invalid_transition", "این نسخه تحویل شده یا قبلاً لغو شده است.");
            }

            rx.Status = PrescriptionStatus.Cancelled;
            audit.Add("PrescriptionCancelled", "MedicalPrescription", id.ToString());
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequirePermission("prescriptions.cancel");

        var optical = app.MapGroup("/api/optical-prescriptions").WithTags("Prescriptions");

        optical.MapGet("/", async (AppDbContext db, string? q, CancellationToken ct) =>
        {
            var query = db.OpticalPrescriptions.AsNoTracking().Where(p => !p.IsCancelled);
            if (!string.IsNullOrWhiteSpace(q))
            {
                query = query.Where(p => p.RxNumber.StartsWith(q) || p.Patient!.FileNumber.StartsWith(q) || p.Patient.FirstName.StartsWith(q));
            }

            return Results.Ok(await QueryOpticalAsync(query.OrderByDescending(p => p.Id).Take(100), ct));
        }).RequirePermission("opticalrx.view");

        optical.MapPost("/", async (CreateOpticalRxRequest request, AppDbContext db, Idempotency idempotency, NumberService numbers, CurrentUser user, AuditService audit, Outbox outbox, CancellationToken ct) =>
            await idempotency.RunAsync(request, async () =>
            {
                var patientId = await ClinicalEndpoints.PatientOfVisitAsync(db, request.VisitId, ct);
                var rx = new OpticalPrescription
                {
                    RxNumber = $"OP-{await numbers.NextAsync("optical-rx", ct: ct):D7}",
                    PatientId = patientId,
                    VisitId = request.VisitId,
                    DoctorUserId = user.Id,
                    SphereOd = Text.Clean(request.SphereOd),
                    CylinderOd = Text.Clean(request.CylinderOd),
                    AxisOd = Text.Clean(request.AxisOd),
                    AddOd = Text.Clean(request.AddOd),
                    SphereOs = Text.Clean(request.SphereOs),
                    CylinderOs = Text.Clean(request.CylinderOs),
                    AxisOs = Text.Clean(request.AxisOs),
                    AddOs = Text.Clean(request.AddOs),
                    Pd = Text.Clean(request.Pd),
                    Notes = Text.Clean(request.Notes),
                };
                db.OpticalPrescriptions.Add(rx);
                await db.SaveChangesAsync(ct);

                audit.Add("OpticalPrescriptionCreated", "OpticalPrescription", rx.Id.ToString());
                outbox.Add("OpticalPrescriptionCreated", new { prescriptionId = rx.Id }, Groups.Optical, Groups.Doctor);
                await db.SaveChangesAsync(ct);

                var dto = await QueryOpticalAsync(db.OpticalPrescriptions.Where(p => p.Id == rx.Id), ct);
                return (StatusCodes.Status201Created, dto.Single());
            }, ct)).RequirePermission("opticalrx.create");

        optical.MapPost("/{id:long}/cancel", async (long id, int version, AppDbContext db, AuditService audit, CancellationToken ct) =>
        {
            var rx = await db.OpticalPrescriptions.SingleOrDefaultAsync(p => p.Id == id, ct) ?? throw AppException.NotFound();
            db.Entry(rx).Property(p => p.Version).OriginalValue = version;
            rx.IsCancelled = true;
            audit.Add("OpticalPrescriptionCancelled", "OpticalPrescription", id.ToString());
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequirePermission("opticalrx.cancel");
    }

    public static async Task<List<PrescriptionDto>> QueryAsync(AppDbContext db, IQueryable<MedicalPrescription> query, CancellationToken ct)
    {
        var rows = await query.AsNoTracking()
            .Select(p => new { p.Id, p.RxNumber, p.PatientId, p.Patient!.FileNumber, p.Patient.FirstName, p.Patient.LastName, p.VisitId, p.Notes, p.Status, p.CreatedAt, p.Version })
            .ToListAsync(ct);

        var ids = rows.Select(r => r.Id).ToList();
        var items = await db.Set<MedicalPrescriptionItem>().AsNoTracking()
            .Where(i => ids.Contains(i.PrescriptionId))
            .OrderBy(i => i.Id)
            .ToListAsync(ct);

        return rows.Select(r => new PrescriptionDto(
            r.Id, r.RxNumber, r.PatientId, r.FileNumber, $"{r.FirstName} {r.LastName}".Trim(), r.VisitId, r.Notes, r.Status.ToString(), r.CreatedAt, r.Version,
            items.Where(i => i.PrescriptionId == r.Id)
                .Select(i => new RxItemDto(i.Id, i.DrugName, i.Dose, i.Route, i.Duration, i.Instructions, i.Quantity, i.DispensedQuantity))
                .ToList())).ToList();
    }

    public static Task<List<OpticalRxDto>> QueryOpticalAsync(IQueryable<OpticalPrescription> query, CancellationToken ct) =>
        query.AsNoTracking()
            .Select(p => new OpticalRxDto(
                p.Id, p.RxNumber, p.PatientId, p.Patient!.FileNumber, (p.Patient.FirstName + " " + (p.Patient.LastName ?? "")).Trim(), p.VisitId,
                p.SphereOd, p.CylinderOd, p.AxisOd, p.AddOd, p.SphereOs, p.CylinderOs, p.AxisOs, p.AddOs, p.Pd, p.Notes, p.IsCancelled, p.CreatedAt, p.Version))
            .ToListAsync(ct);
}
