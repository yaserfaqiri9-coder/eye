using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Inventory;
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

        // Doctors have no inventory access, so this exposes drug names only: no stock, cost or price.
        medical.MapGet("/drugs", async (AppDbContext db, string? q, CancellationToken ct) =>
        {
            var term = q?.Trim();
            if (string.IsNullOrEmpty(term))
            {
                return Results.Ok(Array.Empty<string>());
            }

            // Persian and Arabic keyboards type these letters differently, and either form may be stored.
            var escaped = term.Replace("|", "||").Replace("%", "|%").Replace("_", "|_");
            var persian = escaped.Replace('ي', 'ی').Replace('ك', 'ک') + "%";
            var arabic = escaped.Replace('ی', 'ي').Replace('ک', 'ك') + "%";

            // LIKE follows the column's case-insensitive collation, so "t" finds "Timolol"; StartsWith here would not.
            var names = await db.Products.AsNoTracking()
                .Where(p => p.IsActive && p.Kind == ProductKind.Drug && (EF.Functions.Like(p.Name, persian, "|") || EF.Functions.Like(p.Name, arabic, "|")))
                .Select(p => p.Name)
                .Distinct()
                .OrderBy(name => name)
                .Take(30)
                .ToListAsync(ct);
            return Results.Ok(names);
        }).RequirePermission("prescriptions.create");

        medical.MapPost("/", async (CreatePrescriptionRequest request, AppDbContext db, ClinicalAccess access, Idempotency idempotency, NumberService numbers, CurrentUser user, AuditService audit, Outbox outbox, CancellationToken ct) =>
            await idempotency.RunAsync(request, async () =>
            {
                var patientId = await ClinicalEndpoints.PatientOfVisitAsync(db, request.VisitId, ct);
                await access.EnsureResponsibleAsync(request.VisitId, ct);
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

        medical.MapPut("/{id:long}", async (long id, EditPrescriptionRequest request, AppDbContext db, ClinicalAccess access, CurrentUser user, AuditService audit, Outbox outbox, CancellationToken ct) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            // The same lock the pharmacy takes to dispense, so a change cannot slip in while a drug is being handed over.
            var rx = await db.MedicalPrescriptions.FromSqlInterpolated($"SELECT * FROM MedicalPrescriptions WHERE Id = {id} FOR UPDATE").SingleOrDefaultAsync(ct) ?? throw AppException.NotFound();
            await access.EnsureResponsibleAsync(rx.VisitId, ct);
            db.Entry(rx).Property(p => p.Version).OriginalValue = request.Version;
            var items = await db.Set<MedicalPrescriptionItem>().FromSqlInterpolated($"SELECT * FROM MedicalPrescriptionItem WHERE PrescriptionId = {id} FOR UPDATE").ToListAsync(ct);

            // A sale points at these lines by id, even after it was returned.
            if (rx.Status != PrescriptionStatus.Issued || items.Any(i => i.DispensedQuantity > 0) || await db.Sales.AnyAsync(s => s.PrescriptionId == id, ct))
            {
                throw new AppException(StatusCodes.Status409Conflict, "rx_in_use", "این نسخه لغو یا تحویل شده است و ویرایش نمی‌شود. در صورت نیاز نسخه تازه صادر کنید.");
            }

            var reason = await ClinicalEndpoints.AmendReasonAsync(db, rx.VisitId, request.Reason, ct);
            var replacements = request.Items.Select(i => new MedicalPrescriptionItem
            {
                PrescriptionId = id,
                DrugName = i.DrugName.Trim(),
                Dose = Text.Clean(i.Dose),
                Route = Text.Clean(i.Route),
                Duration = Text.Clean(i.Duration),
                Instructions = Text.Clean(i.Instructions),
                Quantity = i.Quantity,
            }).ToList();

            Amendments.AddChanges(db, nameof(MedicalPrescription), id,
            [
                (nameof(rx.Items), Describe(items), Describe(replacements)),
                (nameof(rx.Notes), rx.Notes, Text.Clean(request.Notes)),
            ], reason, user.Id);

            db.RemoveRange(items);
            db.AddRange(replacements);
            rx.Notes = Text.Clean(request.Notes);

            // The lines live in their own table: a change to only them must still move the version on.
            db.Entry(rx).State = EntityState.Modified;
            audit.Add("PrescriptionEdited", "MedicalPrescription", id.ToString(), reason);
            outbox.Add("PrescriptionCreated", new { prescriptionId = id }, Groups.Pharmacy, Groups.Doctor);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Ok((await QueryAsync(db, db.MedicalPrescriptions.Where(p => p.Id == id), ct)).Single());
        }).RequirePermission("prescriptions.edit");

        medical.MapPost("/{id:long}/cancel", async (long id, int version, AppDbContext db, ClinicalAccess access, AuditService audit, CancellationToken ct) =>
        {
            var rx = await db.MedicalPrescriptions.SingleOrDefaultAsync(p => p.Id == id, ct) ?? throw AppException.NotFound();
            await access.EnsureResponsibleAsync(rx.VisitId, ct);
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

        optical.MapPost("/", async (CreateOpticalRxRequest request, AppDbContext db, ClinicalAccess access, Idempotency idempotency, NumberService numbers, CurrentUser user, AuditService audit, Outbox outbox, CancellationToken ct) =>
            await idempotency.RunAsync(request, async () =>
            {
                var patientId = await ClinicalEndpoints.PatientOfVisitAsync(db, request.VisitId, ct);
                await access.EnsureResponsibleAsync(request.VisitId, ct);
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

        optical.MapPut("/{id:long}", async (long id, EditOpticalRxRequest request, AppDbContext db, ClinicalAccess access, CurrentUser user, AuditService audit, Outbox outbox, CancellationToken ct) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var rx = await db.OpticalPrescriptions.FromSqlInterpolated($"SELECT * FROM OpticalPrescriptions WHERE Id = {id} FOR UPDATE").SingleOrDefaultAsync(ct) ?? throw AppException.NotFound();
            await access.EnsureResponsibleAsync(rx.VisitId, ct);
            db.Entry(rx).Property(p => p.Version).OriginalValue = request.Version;

            // Lenses may already be in the workshop to these numbers.
            if (rx.IsCancelled || await db.OpticalOrders.AnyAsync(o => o.OpticalPrescriptionId == id && o.Status != OpticalOrderStatus.Cancelled, ct))
            {
                throw new AppException(StatusCodes.Status409Conflict, "rx_in_use", "این نسخه لغو شده یا سفارش عینک دارد و ویرایش نمی‌شود. در صورت نیاز نسخه تازه صادر کنید.");
            }

            var reason = await ClinicalEndpoints.AmendReasonAsync(db, rx.VisitId, request.Reason, ct);
            Amendments.AddChanges(db, nameof(OpticalPrescription), id,
            [
                (nameof(rx.SphereOd), rx.SphereOd, Text.Clean(request.SphereOd)),
                (nameof(rx.CylinderOd), rx.CylinderOd, Text.Clean(request.CylinderOd)),
                (nameof(rx.AxisOd), rx.AxisOd, Text.Clean(request.AxisOd)),
                (nameof(rx.AddOd), rx.AddOd, Text.Clean(request.AddOd)),
                (nameof(rx.SphereOs), rx.SphereOs, Text.Clean(request.SphereOs)),
                (nameof(rx.CylinderOs), rx.CylinderOs, Text.Clean(request.CylinderOs)),
                (nameof(rx.AxisOs), rx.AxisOs, Text.Clean(request.AxisOs)),
                (nameof(rx.AddOs), rx.AddOs, Text.Clean(request.AddOs)),
                (nameof(rx.Pd), rx.Pd, Text.Clean(request.Pd)),
                (nameof(rx.Notes), rx.Notes, Text.Clean(request.Notes)),
            ], reason, user.Id);

            rx.SphereOd = Text.Clean(request.SphereOd);
            rx.CylinderOd = Text.Clean(request.CylinderOd);
            rx.AxisOd = Text.Clean(request.AxisOd);
            rx.AddOd = Text.Clean(request.AddOd);
            rx.SphereOs = Text.Clean(request.SphereOs);
            rx.CylinderOs = Text.Clean(request.CylinderOs);
            rx.AxisOs = Text.Clean(request.AxisOs);
            rx.AddOs = Text.Clean(request.AddOs);
            rx.Pd = Text.Clean(request.Pd);
            rx.Notes = Text.Clean(request.Notes);
            audit.Add("OpticalPrescriptionEdited", "OpticalPrescription", id.ToString(), reason);
            outbox.Add("OpticalPrescriptionCreated", new { prescriptionId = id }, Groups.Optical, Groups.Doctor);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Ok((await QueryOpticalAsync(db.OpticalPrescriptions.Where(p => p.Id == id), ct)).Single());
        }).RequirePermission("opticalrx.edit");

        optical.MapPost("/{id:long}/cancel", async (long id, int version, AppDbContext db, ClinicalAccess access, AuditService audit, CancellationToken ct) =>
        {
            var rx = await db.OpticalPrescriptions.SingleOrDefaultAsync(p => p.Id == id, ct) ?? throw AppException.NotFound();
            await access.EnsureResponsibleAsync(rx.VisitId, ct);
            db.Entry(rx).Property(p => p.Version).OriginalValue = version;
            rx.IsCancelled = true;
            audit.Add("OpticalPrescriptionCancelled", "OpticalPrescription", id.ToString());
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequirePermission("opticalrx.cancel");
    }

    private static string Describe(IEnumerable<MedicalPrescriptionItem> items) =>
        string.Join("\n", items.Select(i => string.Join(" — ", new[] { i.DrugName, i.Dose, i.Route, i.Duration, i.Instructions, $"×{i.Quantity:0.###}" }.Where(p => !string.IsNullOrEmpty(p)))));

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
