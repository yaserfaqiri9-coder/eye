using AlShifa.Api.Features.Clinical;
using AlShifa.Api.Features.Finance;
using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AlShifa.Api.Features.Inventory;

public sealed record SaleLineInput(int ProductId, decimal Quantity, long? PrescriptionItemId = null);

public sealed record CustomLineInput(string Description, decimal Amount);

public sealed class StockService(AppDbContext db, CurrentUser user)
{
    /// <summary>
    /// Locks the product's usable batches, earliest expiry first, and takes the quantity from them.
    /// The lock makes concurrent sales queue up, and the CHECK constraint is the last line of defence.
    /// </summary>
    public async Task<List<(StockBatch Batch, decimal Taken)>> TakeAsync(int productId, int locationId, decimal quantity, string sourceType, long sourceId, MovementKind kind, string? reason, CancellationToken ct)
    {
        var today = KabulClock.Today.ToDateTime(TimeOnly.MinValue);
        var batches = await db.StockBatches.FromSqlInterpolated($"""
            SELECT * FROM StockBatches
            WHERE ProductId = {productId} AND LocationId = {locationId} AND Quantity > 0 AND (ExpiryDate IS NULL OR ExpiryDate >= {today})
            ORDER BY ExpiryDate IS NULL, ExpiryDate, Id
            FOR UPDATE
            """).ToListAsync(ct);

        if (batches.Sum(b => b.Quantity) < quantity)
        {
            throw new AppException(StatusCodes.Status409Conflict, "insufficient_stock", "موجودی این جنس کافی نیست.");
        }

        var taken = new List<(StockBatch, decimal)>();
        var remaining = quantity;
        foreach (var batch in batches)
        {
            if (remaining == 0)
            {
                break;
            }

            var part = Math.Min(batch.Quantity, remaining);
            batch.Quantity -= part;
            remaining -= part;
            taken.Add((batch, part));
            AddMovement(batch, kind, -part, sourceType, sourceId, reason);
        }

        return taken;
    }

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
        if (prescriptionId is { } rxId)
        {
            prescription = await db.MedicalPrescriptions.Include(p => p.Items).SingleOrDefaultAsync(p => p.Id == rxId, ct)
                ?? throw new AppException(StatusCodes.Status400BadRequest, "unknown_prescription", "نسخه پیدا نشد.");
            if (prescription.Status is PrescriptionStatus.Cancelled or PrescriptionStatus.Dispensed)
            {
                throw new AppException(StatusCodes.Status409Conflict, "prescription_closed", "این نسخه لغو یا کامل تحویل شده است.");
            }

            if (patientId != prescription.PatientId)
            {
                throw new AppException(StatusCodes.Status400BadRequest, "prescription_mismatch", "این نسخه به این مریض تعلق ندارد.");
            }

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

        decimal totalCost = 0;

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
                if (rxItem.DispensedQuantity + line.Quantity > rxItem.Quantity)
                {
                    throw new AppException(StatusCodes.Status409Conflict, "over_dispense", "مقدار تحویل از مقدار نسخه بیشتر است.");
                }

                rxItem.DispensedQuantity += line.Quantity;
            }

            var taken = await stock.TakeAsync(product.Id, locationId, line.Quantity, nameof(Sale), sale.Id, MovementKind.Sale, null, ct);
            foreach (var (batch, part) in taken)
            {
                sale.Items.Add(new SaleItem { ProductId = product.Id, BatchId = batch.Id, PrescriptionItemId = line.PrescriptionItemId, Quantity = part, UnitPrice = product.SalePrice, UnitCost = batch.UnitCost });
                totalCost += decimal.Round(part * batch.UnitCost, 2, MidpointRounding.AwayFromZero);
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

        if (prescription is not null)
        {
            prescription.Status = prescription.Items.All(i => i.DispensedQuantity >= i.Quantity) ? PrescriptionStatus.Dispensed
                : prescription.Items.Any(i => i.DispensedQuantity > 0) ? PrescriptionStatus.PartiallyDispensed
                : prescription.Status;
        }

        ledger.Post("Cogs", nameof(Sale), sale.Id, sale.SaleNumber,
            (Accounts.CostOfGoods, totalCost, 0),
            (Accounts.Inventory, 0, totalCost));
        audit.Add("SaleCreated", nameof(Sale), sale.Id.ToString());
        outbox.Add("InventoryChanged", new { saleId = sale.Id }, Groups.Pharmacy, Groups.Optical, Groups.Inventory);
        await db.SaveChangesAsync(ct);
        return sale;
    }

    /// <summary>Whole-sale return of an unpaid sale. A paid sale needs an approved refund through finance first.</summary>
    public async Task ReturnAsync(long saleId, int version, string reason, CancellationToken ct)
    {
        var sale = await db.Sales.Include(s => s.Items).SingleOrDefaultAsync(s => s.Id == saleId, ct) ?? throw AppException.NotFound();
        db.Entry(sale).Property(s => s.Version).OriginalValue = version;
        if (sale.IsReturned)
        {
            throw new AppException(StatusCodes.Status409Conflict, "already_returned", "این فروش قبلاً برگشت شده است.");
        }

        var invoice = await invoices.LockAsync(sale.InvoiceId, ct);
        if (invoice.Paid - invoice.Refunded > 0)
        {
            throw new AppException(StatusCodes.Status409Conflict, "sale_is_paid", "این فروش پرداخت شده است. ابتدا برگشت وجه توسط حسابداری ثبت شود.");
        }

        foreach (var item in sale.Items.OrderBy(i => i.BatchId))
        {
            var batch = await db.StockBatches.FromSqlInterpolated($"SELECT * FROM StockBatches WHERE Id = {item.BatchId} FOR UPDATE").SingleAsync(ct);
            batch.Quantity += item.Quantity;
            stock.AddMovement(batch, MovementKind.SaleReturn, item.Quantity, nameof(Sale), sale.Id, reason);

            if (item.PrescriptionItemId is { } rxItemId)
            {
                var rxItem = await db.Set<MedicalPrescriptionItem>().SingleAsync(i => i.Id == rxItemId, ct);
                rxItem.DispensedQuantity -= item.Quantity;
            }
        }

        if (sale.PrescriptionId is { } rxId)
        {
            var rx = await db.MedicalPrescriptions.Include(p => p.Items).SingleAsync(p => p.Id == rxId, ct);
            rx.Status = rx.Items.Any(i => i.DispensedQuantity > 0) ? PrescriptionStatus.PartiallyDispensed : PrescriptionStatus.Issued;
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

        await ledger.ReverseAsync("Cogs", nameof(Sale), sale.Id, ct);
        sale.IsReturned = true;
        audit.Add("SaleReturned", nameof(Sale), sale.Id.ToString(), reason);
        outbox.Add("InventoryChanged", new { saleId = sale.Id }, Groups.Pharmacy, Groups.Optical, Groups.Inventory);
        await db.SaveChangesAsync(ct);
    }
}
