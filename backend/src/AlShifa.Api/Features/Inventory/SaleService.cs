using AlShifa.Api.Features.Clinical;
using AlShifa.Api.Features.Finance;
using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AlShifa.Api.Features.Inventory;

public sealed record SaleLineInput(int ProductId, decimal Quantity, long? PrescriptionItemId = null);

public sealed record CustomLineInput(string Description, decimal Amount);

public sealed class StockService(AppDbContext db, CurrentUser user, IConfiguration config)
{
    /// <summary>How long an unpaid pharmacy bill holds its stock. After that the stock is free again and delivery takes whatever is left.</summary>
    public TimeSpan ReservationWindow => TimeSpan.FromHours(Math.Max(1, config.GetValue("Pharmacy:ReservationHours", 24)));

    /// <summary>
    /// Quantities held by undelivered pharmacy bills, per batch. The caller holds the batch rows locked.
    /// A locking read: a plain one would see the transaction's older snapshot and miss a bill committed while it waited for the lock.
    /// </summary>
    public async Task<Dictionary<long, decimal>> ReservedAsync(IReadOnlyCollection<long> batchIds, long? exceptSaleId, CancellationToken ct)
    {
        if (batchIds.Count == 0)
        {
            return [];
        }

        var since = DateTime.UtcNow - ReservationWindow;
        var rows = await db.Database.SqlQueryRaw<Held>(
            "SELECT si.BatchId AS `Key`, si.Quantity AS `Quantity` FROM SaleItem si JOIN Sales s ON s.Id = si.SaleId " +
            $"WHERE si.BatchId IN ({string.Join(',', batchIds)}) AND si.SaleId <> {{0}} AND s.IsReturned = 0 AND s.DeliveredAt IS NULL AND s.CreatedAt >= {{1}} " +
            "FOR SHARE",
            exceptSaleId ?? -1, since).ToListAsync(ct);
        return rows.GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.Sum(r => r.Quantity));
    }

    /// <summary>One held line read by <see cref="ReservedAsync"/> or <see cref="SaleService.PendingByItemAsync"/>.</summary>
    public sealed class Held
    {
        public long Key { get; set; }

        public decimal Quantity { get; set; }
    }

    /// <summary>What a locked batch can still give without touching stock promised to a pharmacy bill.</summary>
    public async Task<decimal> FreeAsync(StockBatch batch, CancellationToken ct) =>
        batch.Quantity - (await ReservedAsync([batch.Id], null, ct)).GetValueOrDefault(batch.Id);

    /// <summary>
    /// Locks the product's usable batches, earliest expiry first. The lock makes concurrent sales queue up, and the CHECK constraint is the last line of defence.
    /// Expired batches are never offered, and stock reserved for an unpaid bill is left for that bill.
    /// </summary>
    private async Task<List<(StockBatch Batch, decimal Part)>> AllocateAsync(int productId, int locationId, decimal quantity, CancellationToken ct)
    {
        var today = KabulClock.Today.ToDateTime(TimeOnly.MinValue);
        var batches = await db.StockBatches.FromSqlInterpolated($"""
            SELECT * FROM StockBatches
            WHERE ProductId = {productId} AND LocationId = {locationId} AND Quantity > 0 AND (ExpiryDate IS NULL OR ExpiryDate >= {today})
            ORDER BY ExpiryDate IS NULL, ExpiryDate, Id
            FOR UPDATE
            """).ToListAsync(ct);

        var reserved = await ReservedAsync(batches.Select(b => b.Id).ToList(), null, ct);
        var free = batches.Select(b => (Batch: b, Free: b.Quantity - reserved.GetValueOrDefault(b.Id))).Where(b => b.Free > 0).ToList();
        if (free.Sum(b => b.Free) < quantity)
        {
            throw new AppException(StatusCodes.Status409Conflict, "insufficient_stock", "موجودی این جنس کافی نیست.");
        }

        var parts = new List<(StockBatch, decimal)>();
        var remaining = quantity;
        foreach (var (batch, available) in free)
        {
            if (remaining == 0)
            {
                break;
            }

            var part = Math.Min(available, remaining);
            remaining -= part;
            parts.Add((batch, part));
        }

        return parts;
    }

    /// <summary>Takes the quantity off the shelf now, earliest expiry first.</summary>
    public async Task<List<(StockBatch Batch, decimal Taken)>> TakeAsync(int productId, int locationId, decimal quantity, string sourceType, long sourceId, MovementKind kind, string? reason, CancellationToken ct)
    {
        var parts = await AllocateAsync(productId, locationId, quantity, ct);
        foreach (var (batch, part) in parts)
        {
            batch.Quantity -= part;
            AddMovement(batch, kind, -part, sourceType, sourceId, reason);
        }

        return parts;
    }

    /// <summary>Picks the batches a bill will be delivered from, without moving any stock.</summary>
    public Task<List<(StockBatch Batch, decimal Part)>> ReserveAsync(int productId, int locationId, decimal quantity, CancellationToken ct) =>
        AllocateAsync(productId, locationId, quantity, ct);

    public void AddMovement(StockBatch batch, MovementKind kind, decimal quantity, string sourceType, long sourceId, string? reason = null) =>
        db.StockMovements.Add(new StockMovement
        {
            Batch = batch,
            Kind = kind,
            Quantity = quantity,
            SourceType = sourceType,
            SourceId = sourceId,
            Reason = reason,
            UserId = user.Id,
        });

    public Task<int> LocationIdAsync(string code, CancellationToken ct) =>
        db.StockLocations.Where(l => l.Code == code).Select(l => l.Id).SingleAsync(ct);
}

/// <summary>
/// A pharmacy sale is a bill first: it reserves stock and goes to the central cashier. Only <see cref="DeliverAsync"/>, after payment,
/// takes stock off the shelf, books its cost and counts the drugs as dispensed. An optical sale is delivered when it is made.
/// </summary>
public sealed class SaleService(AppDbContext db, NumberService numbers, StockService stock, InvoiceService invoices, LedgerService ledger, CurrentUser user, AuditService audit, Outbox outbox)
{
    /// <summary>Must run inside a transaction. Stock, invoice, ledger and prescription change together or not at all.</summary>
    public async Task<Sale> CreateAsync(SaleKind kind, long? patientId, long? prescriptionId, IReadOnlyList<SaleLineInput> lines, IReadOnlyList<CustomLineInput> customLines, CancellationToken ct)
    {
        if (lines.Count == 0 && customLines.Count == 0)
        {
            throw new AppException(StatusCodes.Status400BadRequest, "empty_sale", "حداقل یک قلم لازم است.");
        }

        if (patientId is { } pid && !await db.Patients.AnyAsync(p => p.Id == pid, ct))
        {
            throw new AppException(StatusCodes.Status400BadRequest, "unknown_patient", "مریض پیدا نشد.");
        }

        var locationId = await stock.LocationIdAsync(kind == SaleKind.Pharmacy ? "pharmacy" : "optical", ct);
        var revenue = kind == SaleKind.Pharmacy ? Accounts.PharmacyRevenue : Accounts.OpticalRevenue;

        long? visitId = null;
        MedicalPrescription? prescription = null;
        Dictionary<long, decimal> billed = [];
        if (prescriptionId is { } rxId)
        {
            prescription = await LockPrescriptionAsync(rxId, ct);
            if (patientId != prescription.PatientId)
            {
                throw new AppException(StatusCodes.Status400BadRequest, "prescription_mismatch", "این نسخه به این مریض تعلق ندارد.");
            }

            EnsureDispensable(prescription);
            billed = await PendingByItemAsync(rxId, null, ct);
            visitId = prescription.VisitId;
        }

        var invoice = await invoices.CreateAsync(patientId, visitId, ct);
        var sale = new Sale
        {
            SaleNumber = $"SL-{await numbers.NextAsync("sale", ct: ct):D7}",
            Kind = kind,
            PatientId = patientId,
            PrescriptionId = prescriptionId,
            InvoiceId = invoice.Id,
            CreatedByUserId = user.Id,
        };
        db.Sales.Add(sale);
        await db.SaveChangesAsync(ct);

        // A fixed product order keeps two concurrent multi-item sales from deadlocking.
        foreach (var line in lines.OrderBy(l => l.ProductId))
        {
            if (line.Quantity <= 0)
            {
                throw new AppException(StatusCodes.Status400BadRequest, "invalid_amount", "مقدار معتبر نیست.");
            }

            var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(p => p.Id == line.ProductId && p.IsActive, ct)
                ?? throw new AppException(StatusCodes.Status400BadRequest, "unknown_product", "جنس انتخاب‌شده معتبر نیست.");

            if (line.PrescriptionItemId is { } itemId)
            {
                var rxItem = prescription?.Items.SingleOrDefault(i => i.Id == itemId)
                    ?? throw new AppException(StatusCodes.Status400BadRequest, "prescription_mismatch", "این قلم به این نسخه تعلق ندارد.");

                // The doctor named the drug; giving another one is the doctor's decision, not the pharmacy's.
                if (!SameDrug(product.Name, rxItem.DrugName))
                {
                    throw new AppException(StatusCodes.Status409Conflict, "substitution_not_allowed", $"«{product.Name}» همان دوای نسخه («{rxItem.DrugName}») نیست. جایگزینی فقط با تأیید داکتر ممکن است.");
                }

                // Lines on bills not yet delivered count too, so the same drug cannot be billed twice.
                if (rxItem.DispensedQuantity + billed.GetValueOrDefault(itemId) + line.Quantity > rxItem.Quantity)
                {
                    throw new AppException(StatusCodes.Status409Conflict, "over_dispense", "مقدار تحویل از مقدار نسخه بیشتر است.");
                }

                billed[itemId] = billed.GetValueOrDefault(itemId) + line.Quantity;
            }

            var parts = kind == SaleKind.Pharmacy
                ? await stock.ReserveAsync(product.Id, locationId, line.Quantity, ct)
                : await stock.TakeAsync(product.Id, locationId, line.Quantity, nameof(Sale), sale.Id, MovementKind.Sale, null, ct);
            foreach (var (batch, part) in parts)
            {
                sale.Items.Add(new SaleItem { ProductId = product.Id, BatchId = batch.Id, PrescriptionItemId = line.PrescriptionItemId, Quantity = part, UnitPrice = product.SalePrice, UnitCost = batch.UnitCost });
            }

            await db.SaveChangesAsync(ct);
            foreach (var item in sale.Items.Where(i => i.ProductId == product.Id))
            {
                await invoices.AddLineAsync(invoice, new ChargeLine(product.Name, item.Quantity, item.UnitPrice, revenue, null, nameof(SaleItem), item.Id), ct);
            }
        }

        foreach (var custom in customLines)
        {
            if (custom.Amount < 0 || string.IsNullOrWhiteSpace(custom.Description))
            {
                throw new AppException(StatusCodes.Status400BadRequest, "invalid_amount", "مبلغ معتبر نیست.");
            }

            await invoices.AddLineAsync(invoice, new ChargeLine(custom.Description.Trim(), 1, custom.Amount, revenue, null, null, null), ct);
        }

        if (kind == SaleKind.Optical)
        {
            var cost = sale.Items.Sum(i => decimal.Round(i.Quantity * i.UnitCost, 2, MidpointRounding.AwayFromZero));
            ledger.Post("Cogs", nameof(Sale), sale.Id, sale.SaleNumber,
                (Accounts.CostOfGoods, cost, 0),
                (Accounts.Inventory, 0, cost));
            Delivered(sale, null);
        }

        if (prescription is not null && prescription.PharmacyStatus != PharmacyRxStatus.AwaitingDecision)
        {
            SetPharmacyStatus(prescription, PharmacyRxStatus.AwaitingDecision, $"بل {sale.SaleNumber}");
        }

        audit.Add("SaleCreated", nameof(Sale), sale.Id.ToString());
        outbox.Add("InventoryChanged", new { saleId = sale.Id }, Groups.Pharmacy, Groups.Optical, Groups.Inventory);
        await db.SaveChangesAsync(ct);
        return sale;
    }

    /// <summary>
    /// Hands a paid pharmacy bill over: takes its reserved batches off the shelf, books the cost and counts the drugs as dispensed. Must run inside a transaction.
    /// An unpaid bill goes out only with a reason, and the endpoint lets only pharmacy.approve give one.
    /// </summary>
    public async Task DeliverAsync(long saleId, int version, string? unpaidReason, CancellationToken ct)
    {
        var sale = (await db.Sales.FromSqlInterpolated($"SELECT * FROM Sales WHERE Id = {saleId} FOR UPDATE").ToListAsync(ct)).SingleOrDefault() ?? throw AppException.NotFound();
        db.Entry(sale).Property(s => s.Version).OriginalValue = version;
        if (sale.Kind != SaleKind.Pharmacy)
        {
            throw AppException.NotFound();
        }

        if (sale.IsReturned || sale.DeliveredAt is not null)
        {
            throw new AppException(StatusCodes.Status409Conflict, "already_closed", sale.IsReturned ? "این بل لغو شده است." : "این بل قبلاً تحویل شده است.");
        }

        var invoice = await invoices.LockAsync(sale.InvoiceId, ct);
        if (!InvoiceService.IsSettled(invoice) && unpaidReason is null)
        {
            throw new AppException(StatusCodes.Status409Conflict, "payment_required", "این بل هنوز در صندوق پرداخت نشده است.");
        }

        MedicalPrescription? prescription = null;
        if (sale.PrescriptionId is { } rxId)
        {
            prescription = await LockPrescriptionAsync(rxId, ct);
            if (prescription.Status == PrescriptionStatus.Cancelled)
            {
                throw new AppException(StatusCodes.Status409Conflict, "prescription_closed", "داکتر این نسخه را لغو کرده است. بل را لغو یا وجه را برگشت کنید.");
            }
        }

        var items = await db.Set<SaleItem>().Where(i => i.SaleId == saleId).OrderBy(i => i.BatchId).ToListAsync(ct);
        var today = KabulClock.Today;
        decimal totalCost = 0;
        foreach (var item in items)
        {
            var batch = await db.StockBatches.FromSqlInterpolated($"SELECT * FROM StockBatches WHERE Id = {item.BatchId} FOR UPDATE").SingleAsync(ct);
            if (batch.ExpiryDate is { } expiry && expiry < today)
            {
                throw new AppException(StatusCodes.Status409Conflict, "batch_expired", "تاریخ بچ این بل گذشته است و تحویل نمی‌شود. بل را لغو و دوباره صادر کنید.");
            }

            var others = (await stock.ReservedAsync([batch.Id], saleId, ct)).GetValueOrDefault(batch.Id);
            if (batch.Quantity - others < item.Quantity)
            {
                throw new AppException(StatusCodes.Status409Conflict, "insufficient_stock", "موجودی این بل دیگر در دواخانه نیست. بل را لغو یا وجه را برگشت کنید.");
            }

            batch.Quantity -= item.Quantity;
            stock.AddMovement(batch, MovementKind.Sale, -item.Quantity, nameof(Sale), sale.Id);
            item.UnitCost = batch.UnitCost;
            totalCost += decimal.Round(item.Quantity * batch.UnitCost, 2, MidpointRounding.AwayFromZero);

            if (item.PrescriptionItemId is { } rxItemId)
            {
                var rxItem = prescription!.Items.Single(i => i.Id == rxItemId);
                if (rxItem.DispensedQuantity + item.Quantity > rxItem.Quantity)
                {
                    throw new AppException(StatusCodes.Status409Conflict, "over_dispense", "مقدار تحویل از مقدار نسخه بیشتر است.");
                }

                rxItem.DispensedQuantity += item.Quantity;
            }
        }

        if (prescription is not null)
        {
            prescription.Status = DispenseStatus(prescription);
        }

        ledger.Post("Cogs", nameof(Sale), sale.Id, sale.SaleNumber,
            (Accounts.CostOfGoods, totalCost, 0),
            (Accounts.Inventory, 0, totalCost));
        Delivered(sale, unpaidReason);
        audit.Add(unpaidReason is null ? "SaleDelivered" : "SaleDeliveredUnpaid", nameof(Sale), sale.Id.ToString(), unpaidReason);
        outbox.Add("InventoryChanged", new { saleId = sale.Id }, Groups.Pharmacy, Groups.Inventory);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Whole-sale return of a sale that was never paid. Returning goods after a payment awaits a finance decision (FINANCE-DECISIONS.md).</summary>
    public async Task ReturnAsync(long saleId, int version, string reason, CancellationToken ct)
    {
        var sale = await db.Sales.Include(s => s.Items).SingleOrDefaultAsync(s => s.Id == saleId, ct) ?? throw AppException.NotFound();
        db.Entry(sale).Property(s => s.Version).OriginalValue = version;
        if (sale.IsReturned)
        {
            throw new AppException(StatusCodes.Status409Conflict, "already_returned", "این فروش قبلاً برگشت شده است.");
        }

        var invoice = await invoices.LockAsync(sale.InvoiceId, ct);
        if (invoice.Paid > 0)
        {
            throw new AppException(StatusCodes.Status409Conflict, "sale_is_paid", "این فروش پرداخت شده است و از اینجا برگشت نمی‌شود. به حسابداری مراجعه کنید.");
        }

        // An undelivered bill moved no stock and dispensed nothing: cancelling it only frees its reservation.
        var delivered = sale.DeliveredAt is not null;
        if (delivered)
        {
            foreach (var item in sale.Items.OrderBy(i => i.BatchId))
            {
                var batch = await db.StockBatches.FromSqlInterpolated($"SELECT * FROM StockBatches WHERE Id = {item.BatchId} FOR UPDATE").SingleAsync(ct);
                batch.Quantity += item.Quantity;
                stock.AddMovement(batch, MovementKind.SaleReturn, item.Quantity, nameof(Sale), sale.Id, reason);

                if (item.PrescriptionItemId is { } rxItemId)
                {
                    var rxItem = await db.Set<MedicalPrescriptionItem>().FromSqlInterpolated($"SELECT * FROM MedicalPrescriptionItem WHERE Id = {rxItemId} FOR UPDATE").SingleAsync(ct);
                    rxItem.DispensedQuantity -= item.Quantity;
                }
            }

            if (sale.PrescriptionId is { } rxId)
            {
                var rx = await db.MedicalPrescriptions.Include(p => p.Items).SingleAsync(p => p.Id == rxId, ct);
                if (rx.Status != PrescriptionStatus.Cancelled)
                {
                    rx.Status = DispenseStatus(rx);
                }
            }
        }

        var lines = await db.InvoiceItems.Where(i => i.InvoiceId == invoice.Id && !i.IsVoid).ToListAsync(ct);
        foreach (var line in lines)
        {
            line.IsVoid = true;
            invoice.Total -= line.LineTotal;
            await ledger.ReverseAsync("Charge", "InvoiceItem", line.Id, ct);
        }

        if (invoice.Discount == 0 && invoice.Paid == 0)
        {
            invoice.Status = InvoiceStatus.Void;
        }

        if (delivered)
        {
            await ledger.ReverseAsync("Cogs", nameof(Sale), sale.Id, ct);
        }

        sale.IsReturned = true;
        audit.Add("SaleReturned", nameof(Sale), sale.Id.ToString(), reason);
        outbox.Add("InventoryChanged", new { saleId = sale.Id }, Groups.Pharmacy, Groups.Optical, Groups.Inventory, Groups.Cashier);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Records a pharmacy decision on the prescription with who made it. The caller saves.</summary>
    public void SetPharmacyStatus(MedicalPrescription prescription, PharmacyRxStatus status, string? note)
    {
        prescription.PharmacyStatus = status;
        db.PrescriptionPharmacyEvents.Add(new PrescriptionPharmacyEvent { PrescriptionId = prescription.Id, Status = status, Note = note, UserId = user.Id });
        db.Entry(prescription).State = EntityState.Modified;
    }

    /// <summary>Doctor's limits the pharmacy cannot pass on its own.</summary>
    public static void EnsureDispensable(MedicalPrescription prescription)
    {
        if (prescription.Status is PrescriptionStatus.Cancelled or PrescriptionStatus.Dispensed)
        {
            throw new AppException(StatusCodes.Status409Conflict, "prescription_closed", "این نسخه لغو یا کامل تحویل شده است.");
        }

        if (prescription.PharmacyStatus == PharmacyRxStatus.NeedsDoctorReview)
        {
            throw new AppException(StatusCodes.Status409Conflict, "doctor_review_required", "این نسخه منتظر تأیید دوباره داکتر است.");
        }

        if (prescription.ValidUntil is { } until && until < KabulClock.Today)
        {
            throw new AppException(StatusCodes.Status409Conflict, "prescription_expired", "اعتبار این نسخه که داکتر تعیین کرده تمام شده است. تأیید دوباره داکتر لازم است.");
        }
    }

    /// <summary>Quantities on bills of this prescription that are neither delivered nor cancelled, per prescription line. A locking read, like the stock reservation.</summary>
    public async Task<Dictionary<long, decimal>> PendingByItemAsync(long prescriptionId, long? exceptSaleId, CancellationToken ct)
    {
        var rows = await db.Database.SqlQueryRaw<StockService.Held>(
            "SELECT si.PrescriptionItemId AS `Key`, si.Quantity AS `Quantity` FROM SaleItem si JOIN Sales s ON s.Id = si.SaleId " +
            "WHERE si.PrescriptionItemId IS NOT NULL AND s.PrescriptionId = {0} AND si.SaleId <> {1} AND s.IsReturned = 0 AND s.DeliveredAt IS NULL FOR SHARE",
            prescriptionId, exceptSaleId ?? -1).ToListAsync(ct);
        return rows.GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.Sum(r => r.Quantity));
    }

    /// <summary>Same drug as written, ignoring case, spacing and the Arabic forms of ی and ک.</summary>
    public static bool SameDrug(string productName, string drugName) =>
        string.Equals(PharmacySearch.Fold(productName), PharmacySearch.Fold(drugName), StringComparison.OrdinalIgnoreCase);

    private async Task<MedicalPrescription> LockPrescriptionAsync(long rxId, CancellationToken ct)
    {
        // Locking reads: pharmacists working on one prescription take turns, and each sees what the previous one did.
        var prescription = (await db.MedicalPrescriptions.FromSqlInterpolated($"SELECT * FROM MedicalPrescriptions WHERE Id = {rxId} FOR UPDATE").ToListAsync(ct)).SingleOrDefault()
            ?? throw new AppException(StatusCodes.Status400BadRequest, "unknown_prescription", "نسخه پیدا نشد.");
        await db.Set<MedicalPrescriptionItem>().FromSqlInterpolated($"SELECT * FROM MedicalPrescriptionItem WHERE PrescriptionId = {rxId} FOR UPDATE").ToListAsync(ct);
        return prescription;
    }

    private static PrescriptionStatus DispenseStatus(MedicalPrescription rx) =>
        rx.Items.All(i => i.DispensedQuantity >= i.Quantity) ? PrescriptionStatus.Dispensed
        : rx.Items.Any(i => i.DispensedQuantity > 0) ? PrescriptionStatus.PartiallyDispensed
        : PrescriptionStatus.Issued;

    private void Delivered(Sale sale, string? unpaidReason)
    {
        sale.DeliveredAt = DateTime.UtcNow;
        sale.DeliveredByUserId = user.Id;
        sale.UnpaidDeliveryReason = unpaidReason;
    }
}
