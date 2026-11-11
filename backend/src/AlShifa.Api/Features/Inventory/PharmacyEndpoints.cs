using System.ComponentModel.DataAnnotations;
using System.Text;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Clinical;
using AlShifa.Api.Features.Finance;
using AlShifa.Api.Features.Patients;
using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AlShifa.Api.Features.Inventory;

public sealed record PharmacyPatientHit(long Id, string FileNumber, string Name, string? FatherName, string? Phone, string Gender, int? Age, int PrescriptionCount, DateTime? LastPrescriptionAt);

public sealed record PharmacyPatient(long Id, string FileNumber, string Name, string? FatherName, string? Phone, string Gender, int? Age);

/// <param name="Stage">Where the prescription stands at the pharmacy, read from its decision, bills and deliveries.</param>
public sealed record PharmacyRxRow(long Id, string RxNumber, long PatientId, string PatientName, string? FatherName, string FileNumber, string? DoctorName, long VisitId, DateTime CreatedAt, DateOnly? ValidUntil, string Status, string PharmacyStatus, string Stage, int Version);

/// <param name="Stock">Available, Short (less than remaining), Out (none usable), NotStocked (no product of that exact name).</param>
public sealed record PharmacyRxItem(long Id, string DrugName, string? Dose, string? Route, string? Duration, string? Instructions,
    decimal Quantity, decimal Dispensed, decimal Billed, decimal Remaining, int? ProductId, string? Unit, decimal? SalePrice, decimal Available, string Stock);

public sealed record PharmacyBill(long Id, string SaleNumber, string InvoiceNumber, decimal Total, decimal Balance, bool Paid, bool IsReturned, DateTime CreatedAt,
    DateTime? DeliveredAt, string? DeliveredBy, string? UnpaidDeliveryReason, bool ReservationExpired, int Version, IReadOnlyList<string> Lines);

public sealed record PharmacyRxEvent(string Status, string? Note, string? UserName, DateTime CreatedAt);

public sealed record PharmacyRxDetail(PharmacyRxRow Prescription, string? Notes, bool Expired, PharmacyPatient Patient, IReadOnlyList<PharmacyRxItem> Items, IReadOnlyList<PharmacyBill> Bills, IReadOnlyList<PharmacyRxEvent> Events);

public sealed record PharmacyQueueBill(long SaleId, string SaleNumber, long? PrescriptionId, string? RxNumber, long? PatientId, string? PatientName, string? FileNumber, string InvoiceNumber, decimal Total, decimal Balance, DateTime CreatedAt, bool ReservationExpired, int Version);

public sealed record PharmacyQueues(IReadOnlyList<PharmacyRxRow> New, IReadOnlyList<PharmacyQueueBill> AwaitingPayment, IReadOnlyList<PharmacyQueueBill> ReadyToDeliver, IReadOnlyList<PharmacyRxRow> Partial, IReadOnlyList<PharmacyRxRow> NeedsDoctor);

public sealed record SetPharmacyStatusRequest(PharmacyRxStatus Status, [MaxLength(300)] string? Note, int Version);

/// <param name="UnpaidReason">Only for handing over an unpaid bill in an emergency; needs pharmacy.approve.</param>
public sealed record DeliverSaleRequest(int Version, [MaxLength(300)] string? UnpaidReason = null);

/// <summary>Search text as people type it: Persian or Arabic letters, Persian, Arabic or Latin digits.</summary>
public static class PharmacySearch
{
    public static string Fold(string value)
    {
        var text = new StringBuilder(value.Length);
        foreach (var c in value.Trim())
        {
            text.Append(c switch
            {
                >= '۰' and <= '۹' => (char)('0' + (c - '۰')),
                >= '٠' and <= '٩' => (char)('0' + (c - '٠')),
                'ي' or 'ى' => 'ی',
                'ك' => 'ک',
                'ة' => 'ه',
                '‌' => ' ',
                _ => c,
            });
        }

        return string.Join(' ', text.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>The folded text and its Arabic-letter twin, since either may be stored. Both are escaped for LIKE with '|'.</summary>
    public static string[] Variants(string folded)
    {
        var escaped = folded.Replace("|", "||").Replace("%", "|%").Replace("_", "|_");
        var arabic = escaped.Replace('ی', 'ي').Replace('ک', 'ك');
        return arabic == escaped ? [escaped] : [escaped, arabic];
    }
}

public static class PharmacyEndpoints
{
    private const int NewDays = 7;

    public static void MapPharmacy(this IEndpointRouteBuilder app)
    {
        var pharmacy = app.MapGroup("/api/pharmacy").WithTags("Pharmacy");

        // Finds a patient by name, father's name, phone, file number, or the number of any prescription, visit, bill or invoice of theirs.
        pharmacy.MapGet("/search", async (AppDbContext db, string? q, CancellationToken ct) =>
        {
            var folded = PharmacySearch.Fold(q ?? "");
            if (folded.Length < 2)
            {
                return Results.Ok(Array.Empty<PharmacyPatientHit>());
            }

            var ids = new HashSet<long>();

            // Numbers people read off paper: P-000123, RX-0000045, V-..., INV-..., SL-..., or only their digits.
            var digits = new string(folded.Where(char.IsAsciiDigit).ToArray());
            var code = folded.ToUpperInvariant();
            if (folded.Any(char.IsAsciiDigit) && !folded.Contains(' '))
            {
                var suffix = "%" + digits;
                ids.UnionWith(await db.Patients.Where(p => p.FileNumber == code || EF.Functions.Like(p.FileNumber, suffix) || (p.Phone != null && EF.Functions.Like(p.Phone, "%" + digits + "%"))).OrderByDescending(p => p.Id).Select(p => p.Id).Take(50).ToListAsync(ct));
                ids.UnionWith(await db.MedicalPrescriptions.Where(p => p.RxNumber == code || EF.Functions.Like(p.RxNumber, suffix)).OrderByDescending(p => p.Id).Select(p => p.PatientId).Take(20).ToListAsync(ct));
                ids.UnionWith(await db.Visits.Where(v => v.VisitNumber == code || EF.Functions.Like(v.VisitNumber, suffix)).OrderByDescending(v => v.Id).Select(v => v.PatientId).Take(20).ToListAsync(ct));
                ids.UnionWith(await db.Sales.Where(s => s.PatientId != null && (s.SaleNumber == code || s.Invoice!.InvoiceNumber == code)).OrderByDescending(s => s.Id).Select(s => s.PatientId!.Value).Take(20).ToListAsync(ct));
            }

            // Every word must appear in the first, last or father's name, so "احمد کریمی" finds Ahmad Karimi but not every Ahmad.
            var words = folded.Split(' ');
            if (words.Any(w => !w.All(char.IsAsciiDigit)))
            {
                var query = db.Patients.AsQueryable();
                foreach (var word in words)
                {
                    var forms = PharmacySearch.Variants(word).Select(v => "%" + v + "%").ToArray();
                    var first = forms[0];
                    var second = forms.Length > 1 ? forms[1] : forms[0];
                    query = query.Where(p =>
                        EF.Functions.Like(p.FirstName, first, "|") || EF.Functions.Like(p.FirstName, second, "|") ||
                        (p.LastName != null && (EF.Functions.Like(p.LastName, first, "|") || EF.Functions.Like(p.LastName, second, "|"))) ||
                        (p.FatherName != null && (EF.Functions.Like(p.FatherName, first, "|") || EF.Functions.Like(p.FatherName, second, "|"))));
                }

                ids.UnionWith(await query.OrderByDescending(p => p.Id).Select(p => p.Id).Take(50).ToListAsync(ct));
            }

            var rows = await db.Patients.AsNoTracking().Where(p => ids.Contains(p.Id))
                .Select(p => new
                {
                    p.Id, p.FileNumber, p.FirstName, p.LastName, p.FatherName, p.Phone, p.Gender, p.BirthDate, p.EstimatedBirthYear,
                    Count = db.MedicalPrescriptions.Count(r => r.PatientId == p.Id),
                    Last = db.MedicalPrescriptions.Where(r => r.PatientId == p.Id).Max(r => (DateTime?)r.CreatedAt),
                })
                .ToListAsync(ct);

            return Results.Ok(rows
                .OrderByDescending(p => p.FileNumber == code)
                .ThenByDescending(p => p.Last)
                .Take(50)
                .Select(p => new PharmacyPatientHit(p.Id, p.FileNumber, $"{p.FirstName} {p.LastName}".Trim(), p.FatherName, p.Phone, p.Gender.ToString(),
                    PatientEndpoints.AgeOf(p.BirthDate, p.EstimatedBirthYear), p.Count, p.Last))
                .ToList());
        }).RequirePermission("prescriptions.view");

        // All of one patient's prescriptions, old ones included, newest first.
        pharmacy.MapGet("/patients/{id:long}/prescriptions", async (long id, AppDbContext db, CancellationToken ct) =>
        {
            var patient = await PatientAsync(db, id, ct);
            var rows = await RowsAsync(db, db.MedicalPrescriptions.Where(p => p.PatientId == id).OrderByDescending(p => p.Id).Take(200), ct);
            return Results.Ok(new { patient, prescriptions = rows });
        }).RequirePermission("prescriptions.view");

        pharmacy.MapGet("/prescriptions/{id:long}", async (long id, AppDbContext db, StockService stock, CancellationToken ct) =>
            Results.Ok(await DetailAsync(db, stock, id, ct))).RequirePermission("prescriptions.view");

        pharmacy.MapGet("/queues", async (AppDbContext db, StockService stock, CancellationToken ct) =>
        {
            var since = DateTime.UtcNow.AddDays(-NewDays);
            var open = db.MedicalPrescriptions.Where(p => p.Status != PrescriptionStatus.Cancelled && p.Status != PrescriptionStatus.Dispensed);

            var fresh = await RowsAsync(db, open
                .Where(p => p.CreatedAt >= since && p.Status == PrescriptionStatus.Issued
                    && (p.PharmacyStatus == PharmacyRxStatus.AwaitingVisit || p.PharmacyStatus == PharmacyRxStatus.AwaitingDecision)
                    && !db.Sales.Any(s => s.PrescriptionId == p.Id && !s.IsReturned && s.DeliveredAt == null))
                .OrderByDescending(p => p.Id).Take(100), ct);
            var partial = await RowsAsync(db, open.Where(p => p.Status == PrescriptionStatus.PartiallyDispensed).OrderByDescending(p => p.Id).Take(100), ct);
            var doctor = await RowsAsync(db, open.Where(p => p.PharmacyStatus == PharmacyRxStatus.NeedsDoctorReview).OrderByDescending(p => p.Id).Take(100), ct);

            var cutoff = DateTime.UtcNow - stock.ReservationWindow;
            var bills = await db.Sales.AsNoTracking()
                .Where(s => s.Kind == SaleKind.Pharmacy && !s.IsReturned && s.DeliveredAt == null)
                .OrderBy(s => s.Id)
                .Select(s => new
                {
                    Row = new PharmacyQueueBill(s.Id, s.SaleNumber, s.PrescriptionId, s.Prescription != null ? s.Prescription.RxNumber : null, s.PatientId,
                        s.Patient != null ? (s.Patient.FirstName + " " + (s.Patient.LastName ?? "")).Trim() : null, s.Patient != null ? s.Patient.FileNumber : null,
                        s.Invoice!.InvoiceNumber, s.Invoice.Total - s.Invoice.Discount, s.Invoice.Total - s.Invoice.Discount - s.Invoice.Paid, s.CreatedAt, s.CreatedAt < cutoff, s.Version),
                    Paid = s.Invoice.Status != InvoiceStatus.Void && s.Invoice.Total - s.Invoice.Discount <= s.Invoice.Paid - s.Invoice.Refunded,
                })
                .Take(300)
                .ToListAsync(ct);

            return Results.Ok(new PharmacyQueues(fresh, bills.Where(b => !b.Paid).Select(b => b.Row).ToList(), bills.Where(b => b.Paid).Select(b => b.Row).ToList(), partial, doctor));
        }).RequirePermission("pharmacy.view");

        // Records what the patient decided. Nothing here touches money or stock, and the prescription itself never changes.
        pharmacy.MapPost("/prescriptions/{id:long}/status", async (long id, SetPharmacyStatusRequest request, AppDbContext db, SaleService sales, AuditService audit, Outbox outbox, CancellationToken ct) =>
        {
            if (request.Status == PharmacyRxStatus.AwaitingVisit)
            {
                throw new AppException(StatusCodes.Status400BadRequest, "invalid_status", "این وضعیت از اینجا انتخاب نمی‌شود.");
            }

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var rx = (await db.MedicalPrescriptions.FromSqlInterpolated($"SELECT * FROM MedicalPrescriptions WHERE Id = {id} FOR UPDATE").ToListAsync(ct)).SingleOrDefault() ?? throw AppException.NotFound();
            db.Entry(rx).Property(p => p.Version).OriginalValue = request.Version;
            if (rx.Status == PrescriptionStatus.Cancelled)
            {
                throw new AppException(StatusCodes.Status409Conflict, "prescription_closed", "این نسخه لغو شده است.");
            }

            // Only the doctor lifts a review the pharmacy asked for (POST /api/prescriptions/{id}/reapprove).
            if (rx.PharmacyStatus == PharmacyRxStatus.NeedsDoctorReview && request.Status != PharmacyRxStatus.NeedsDoctorReview)
            {
                throw new AppException(StatusCodes.Status409Conflict, "doctor_review_required", "این نسخه منتظر تأیید دوباره داکتر است.");
            }

            var note = Text.Clean(request.Note);
            sales.SetPharmacyStatus(rx, request.Status, note);
            audit.Add("PrescriptionPharmacyStatus", nameof(MedicalPrescription), id.ToString(), $"{request.Status}{(note is null ? "" : $": {note}")}");
            outbox.Add("PrescriptionCreated", new { prescriptionId = id }, Groups.Pharmacy, Groups.Doctor);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        }).RequirePermission("pharmacy.edit");

        pharmacy.MapPost("/sales/{id:long}/deliver", async (long id, DeliverSaleRequest request, AppDbContext db, SaleService sales, HttpContext http, CancellationToken ct) =>
        {
            var reason = Text.Clean(request.UnpaidReason);
            if (reason is not null)
            {
                Permission.Require(http, "pharmacy.approve");
                if (reason.Length < 3)
                {
                    throw new AppException(StatusCodes.Status400BadRequest, "reason_required", "دلیل تحویل بدون پرداخت را بنویسید.");
                }
            }

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await sales.DeliverAsync(id, request.Version, reason, ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        }).RequirePermission("pharmacy.create");
    }

    private static async Task<PharmacyPatient> PatientAsync(AppDbContext db, long id, CancellationToken ct)
    {
        var p = await db.Patients.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw AppException.NotFound();
        return new PharmacyPatient(p.Id, p.FileNumber, $"{p.FirstName} {p.LastName}".Trim(), p.FatherName, p.Phone, p.Gender.ToString(), PatientEndpoints.AgeOf(p.BirthDate, p.EstimatedBirthYear));
    }

    private static async Task<List<PharmacyRxRow>> RowsAsync(AppDbContext db, IQueryable<MedicalPrescription> query, CancellationToken ct)
    {
        var rows = await query.AsNoTracking()
            .Select(p => new
            {
                p.Id, p.RxNumber, p.PatientId, p.Patient!.FirstName, p.Patient.LastName, p.Patient.FatherName, p.Patient.FileNumber, p.VisitId, p.CreatedAt, p.ValidUntil,
                p.Status, p.PharmacyStatus, p.Version,
                Doctor = db.Users.Where(u => u.Id == p.DoctorUserId).Select(u => u.FullName).FirstOrDefault(),
                Unpaid = db.Sales.Any(s => s.PrescriptionId == p.Id && !s.IsReturned && s.DeliveredAt == null && !(s.Invoice!.Status != InvoiceStatus.Void && s.Invoice.Total - s.Invoice.Discount <= s.Invoice.Paid - s.Invoice.Refunded)),
                Ready = db.Sales.Any(s => s.PrescriptionId == p.Id && !s.IsReturned && s.DeliveredAt == null && s.Invoice!.Status != InvoiceStatus.Void && s.Invoice.Total - s.Invoice.Discount <= s.Invoice.Paid - s.Invoice.Refunded),
            })
            .ToListAsync(ct);

        return rows.Select(r => new PharmacyRxRow(r.Id, r.RxNumber, r.PatientId, $"{r.FirstName} {r.LastName}".Trim(), r.FatherName, r.FileNumber, r.Doctor, r.VisitId, r.CreatedAt, r.ValidUntil,
            r.Status.ToString(), r.PharmacyStatus.ToString(), Stage(r.Status, r.PharmacyStatus, r.Unpaid, r.Ready, r.ValidUntil), r.Version)).ToList();
    }

    /// <summary>One label for the list. The data behind it stays separate: clinical status, decision, bills and deliveries.</summary>
    private static string Stage(PrescriptionStatus status, PharmacyRxStatus decision, bool unpaid, bool ready, DateOnly? validUntil) =>
        status == PrescriptionStatus.Cancelled ? "Cancelled"
        : status == PrescriptionStatus.Dispensed ? "Delivered"
        : ready ? "ReadyToDeliver"
        : unpaid ? "AwaitingPayment"
        : decision == PharmacyRxStatus.NeedsDoctorReview || validUntil < KabulClock.Today ? "NeedsDoctorReview"
        : status == PrescriptionStatus.PartiallyDispensed ? "Partial"
        : decision.ToString();

    private static async Task<PharmacyRxDetail> DetailAsync(AppDbContext db, StockService stock, long id, CancellationToken ct)
    {
        var row = (await RowsAsync(db, db.MedicalPrescriptions.Where(p => p.Id == id), ct)).SingleOrDefault() ?? throw AppException.NotFound();
        var rx = await db.MedicalPrescriptions.AsNoTracking().Include(p => p.Items).SingleAsync(p => p.Id == id, ct);
        var patient = await PatientAsync(db, rx.PatientId, ct);
        var billed = await db.Set<SaleItem>().AsNoTracking()
            .Where(i => i.PrescriptionItemId != null && db.Sales.Any(s => s.Id == i.SaleId && s.PrescriptionId == id && !s.IsReturned && s.DeliveredAt == null))
            .GroupBy(i => i.PrescriptionItemId!.Value).Select(g => new { g.Key, Quantity = g.Sum(i => i.Quantity) })
            .ToDictionaryAsync(g => g.Key, g => g.Quantity, ct);

        // The product of exactly the prescribed name; anything else would be a substitution.
        var names = rx.Items.Select(i => i.DrugName).Distinct().ToList();
        var products = (await db.Products.AsNoTracking().Where(p => p.IsActive && p.Kind == ProductKind.Drug).Select(p => new { p.Id, p.Name, p.Unit, p.SalePrice }).ToListAsync(ct))
            .Where(p => names.Any(n => SaleService.SameDrug(p.Name, n))).ToList();
        var pharmacyId = await stock.LocationIdAsync("pharmacy", ct);
        var today = KabulClock.Today;
        var batches = await db.StockBatches.AsNoTracking()
            .Where(b => b.LocationId == pharmacyId && b.Quantity > 0 && (b.ExpiryDate == null || b.ExpiryDate >= today) && products.Select(p => p.Id).Contains(b.ProductId))
            .Select(b => new { b.Id, b.ProductId, b.Quantity }).ToListAsync(ct);
        var reserved = await stock.ReservedAsync(batches.Select(b => b.Id).ToList(), null, ct);

        var items = rx.Items.OrderBy(i => i.Id).Select(i =>
        {
            var product = products.FirstOrDefault(p => SaleService.SameDrug(p.Name, i.DrugName));
            var pending = billed.GetValueOrDefault(i.Id);
            var remaining = Math.Max(0, i.Quantity - i.DispensedQuantity - pending);
            var available = product is null ? 0 : batches.Where(b => b.ProductId == product.Id).Sum(b => b.Quantity - reserved.GetValueOrDefault(b.Id));
            var state = product is null ? "NotStocked" : available <= 0 ? "Out" : available < remaining ? "Short" : "Available";
            return new PharmacyRxItem(i.Id, i.DrugName, i.Dose, i.Route, i.Duration, i.Instructions, i.Quantity, i.DispensedQuantity, pending, remaining,
                product?.Id, product?.Unit, product?.SalePrice, Math.Max(0, available), state);
        }).ToList();

        var cutoff = DateTime.UtcNow - stock.ReservationWindow;
        var bills = await db.Sales.AsNoTracking().Where(s => s.PrescriptionId == id).OrderByDescending(s => s.Id)
            .Select(s => new
            {
                s.Id, s.SaleNumber, s.Invoice!.InvoiceNumber, Total = s.Invoice.Total - s.Invoice.Discount, Balance = s.Invoice.Total - s.Invoice.Discount - s.Invoice.Paid,
                Paid = s.Invoice.Status != InvoiceStatus.Void && s.Invoice.Total - s.Invoice.Discount <= s.Invoice.Paid - s.Invoice.Refunded,
                s.IsReturned, s.CreatedAt, s.DeliveredAt, DeliveredBy = db.Users.Where(u => u.Id == s.DeliveredByUserId).Select(u => u.FullName).FirstOrDefault(),
                s.UnpaidDeliveryReason, s.Version,
                Lines = s.Items.Select(i => new { i.Product!.Name, i.Quantity }).ToList(),
            })
            .ToListAsync(ct);

        var events = await db.PrescriptionPharmacyEvents.AsNoTracking().Where(e => e.PrescriptionId == id).OrderByDescending(e => e.Id)
            .Select(e => new PharmacyRxEvent(e.Status.ToString(), e.Note, db.Users.Where(u => u.Id == e.UserId).Select(u => u.FullName).FirstOrDefault(), e.CreatedAt))
            .ToListAsync(ct);

        return new PharmacyRxDetail(row, rx.Notes, rx.ValidUntil < today, patient, items,
            bills.Select(b => new PharmacyBill(b.Id, b.SaleNumber, b.InvoiceNumber, b.Total, b.Balance, b.Paid, b.IsReturned, b.CreatedAt, b.DeliveredAt, b.DeliveredBy,
                b.UnpaidDeliveryReason, !b.IsReturned && b.DeliveredAt == null && b.CreatedAt < cutoff, b.Version, b.Lines.Select(l => $"{l.Name} × {l.Quantity:0.###}").ToList())).ToList(),
            events);
    }
}
