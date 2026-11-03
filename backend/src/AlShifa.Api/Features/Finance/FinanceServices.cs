using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AlShifa.Api.Features.Finance;

/// <summary>Chart of accounts. Provisional until confirmed by the finance lead (WORKFLOWS.md F-4).</summary>
public static class Accounts
{
    public const string Cash = "1000";
    public const string Receivable = "1100";
    public const string Inventory = "1200";
    public const string Payable = "2000";
    public const string ServiceRevenue = "4000";
    public const string PharmacyRevenue = "4100";
    public const string OpticalRevenue = "4200";
    public const string Discounts = "4900";
    public const string Refunds = "4950";
    public const string CostOfGoods = "5000";
    public const string Expenses = "6000";
    public const string InventoryAdjustments = "6100";

    public static readonly (string Code, string Name)[] All =
    [
        (Cash, "صندوق"),
        (Receivable, "حساب‌های دریافتنی"),
        (Inventory, "موجودی اجناس"),
        (Payable, "حساب‌های پرداختنی"),
        (ServiceRevenue, "درآمد خدمات"),
        (PharmacyRevenue, "فروش دواخانه"),
        (OpticalRevenue, "فروش عینک"),
        (Discounts, "تخفیف‌ها"),
        (Refunds, "برگشت وجه"),
        (CostOfGoods, "قیمت تمام‌شده فروش"),
        (Expenses, "مصارف"),
        (InventoryAdjustments, "تعدیل موجودی"),
    ];
}

/// <summary>Only writer of the ledger. Entries are immutable; corrections are posted as reversals.</summary>
public sealed class LedgerService(AppDbContext db, CurrentUser user)
{
    public LedgerTransaction Post(string kind, string sourceType, long sourceId, string? description, params (string Account, decimal Debit, decimal Credit)[] lines)
    {
        var active = lines.Where(l => l.Debit != 0 || l.Credit != 0).ToArray();
        if (active.Any(l => l.Debit < 0 || l.Credit < 0) || active.Sum(l => l.Debit) != active.Sum(l => l.Credit))
        {
            throw new InvalidOperationException($"Unbalanced ledger transaction {kind} {sourceType}:{sourceId}.");
        }

        var transaction = new LedgerTransaction
        {
            Kind = kind,
            SourceType = sourceType,
            SourceId = sourceId,
            Description = description,
            CreatedByUserId = user.Id,
            Entries = active.Select(l => new LedgerEntry { AccountCode = l.Account, Debit = l.Debit, Credit = l.Credit }).ToList(),
        };
        db.LedgerTransactions.Add(transaction);
        return transaction;
    }

    public async Task ReverseAsync(string kind, string sourceType, long sourceId, CancellationToken ct)
    {
        var original = await db.LedgerTransactions.Include(t => t.Entries)
            .SingleAsync(t => t.Kind == kind && t.SourceType == sourceType && t.SourceId == sourceId, ct);

        var reversal = Post($"{kind}Reversal", sourceType, sourceId, null, original.Entries.Select(e => (e.AccountCode, e.Credit, e.Debit)).ToArray());
        reversal.ReversalOfId = original.Id;
    }
}

/// <summary>Services the reception flow charges by code; their prices are set by finance.</summary>
public static class ServiceCodes
{
    public const string VisitFee = "VISIT-FEE";
    public const string Refraction = "REFRACTION";
    public const string Imaging = "IMAGING";

    public static readonly string[] PaidFlow = [VisitFee, Refraction, Imaging];
}

public static class Money
{
    /// <summary>Amounts are stored to two places. Anything finer would be rounded differently in the invoice, payment and ledger rows.</summary>
    public static void EnsureCents(decimal amount)
    {
        if (decimal.Round(amount, 2) != amount)
        {
            throw new AppException(StatusCodes.Status400BadRequest, "invalid_amount", "مبلغ می‌تواند حداکثر دو رقم اعشار داشته باشد.");
        }
    }
}

public sealed record ChargeLine(string Description, decimal Quantity, decimal UnitPrice, string RevenueAccount, int? ServiceId, string? SourceType, long? SourceId);

public sealed class InvoiceService(AppDbContext db, NumberService numbers, LedgerService ledger, CurrentUser user, AuditService audit, Outbox outbox)
{
    /// <summary>Row lock serialises concurrent payments and charges on one invoice.</summary>
    public async Task<Invoice> LockAsync(long id, CancellationToken ct) =>
        await db.Invoices.FromSqlInterpolated($"SELECT * FROM Invoices WHERE Id = {id} FOR UPDATE").SingleOrDefaultAsync(ct)
        ?? throw AppException.NotFound();

    public async Task<Invoice> CreateAsync(long? patientId, long? visitId, CancellationToken ct)
    {
        var invoice = new Invoice
        {
            InvoiceNumber = $"INV-{await numbers.NextAsync("invoice", ct: ct):D7}",
            PatientId = patientId,
            VisitId = visitId,
            IssuedByUserId = user.Id,
        };
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync(ct);
        return invoice;
    }

    /// <summary>Adds a billable line to the visit's open invoice. The ActiveSourceKey index rejects a repeated source.</summary>
    public async Task<InvoiceItem> ChargeVisitAsync(long patientId, long visitId, ChargeLine line, CancellationToken ct)
    {
        var existingId = await db.Invoices
            // A visit fee and a prepaid request each keep their own invoice; a later charge must not reopen it.
            .Where(i => i.VisitId == visitId && i.Status != InvoiceStatus.Void
                && !db.VisitTokens.Any(t => t.InvoiceId == i.Id) && !db.DiagnosticOrders.Any(o => o.InvoiceId == i.Id))
            .OrderByDescending(i => i.Id)
            .Select(i => (long?)i.Id)
            .FirstOrDefaultAsync(ct);

        var invoice = existingId is { } id ? await LockAsync(id, ct) : await CreateAsync(patientId, visitId, ct);
        return await AddLineAsync(invoice, line, ct);
    }

    public async Task<InvoiceItem> AddLineAsync(Invoice invoice, ChargeLine line, CancellationToken ct)
    {
        if (invoice.Status == InvoiceStatus.Void)
        {
            throw new AppException(StatusCodes.Status409Conflict, "invoice_void", "این فاکتور باطل شده است.");
        }

        if (line.Quantity <= 0 || line.UnitPrice < 0)
        {
            throw new AppException(StatusCodes.Status400BadRequest, "invalid_amount", "مقدار یا قیمت معتبر نیست.");
        }

        var item = new InvoiceItem
        {
            InvoiceId = invoice.Id,
            Description = line.Description,
            Quantity = line.Quantity,
            UnitPrice = line.UnitPrice,
            LineTotal = decimal.Round(line.Quantity * line.UnitPrice, 2, MidpointRounding.AwayFromZero),
            ServiceId = line.ServiceId,
            RevenueAccount = line.RevenueAccount,
            SourceType = line.SourceType,
            SourceId = line.SourceId,
        };
        db.InvoiceItems.Add(item);

        invoice.Total += item.LineTotal;
        UpdateStatus(invoice);
        await db.SaveChangesAsync(ct);

        ledger.Post("Charge", "InvoiceItem", item.Id, item.Description,
            (Accounts.Receivable, item.LineTotal, 0),
            (item.RevenueAccount, 0, item.LineTotal));
        outbox.Add("InvoiceIssued", new { invoiceId = invoice.Id }, Groups.Cashier, Groups.Finance, Groups.Reception);
        await db.SaveChangesAsync(ct);
        return item;
    }

    public async Task VoidLineAsync(InvoiceItem item, CancellationToken ct)
    {
        var invoice = await LockAsync(item.InvoiceId, ct);
        if (invoice.Total - invoice.Discount - item.LineTotal < invoice.Paid)
        {
            throw new AppException(StatusCodes.Status409Conflict, "line_already_paid", "مبلغ این آیتم پرداخت شده است. ابتدا برگشت وجه ثبت شود.");
        }

        item.IsVoid = true;
        invoice.Total -= item.LineTotal;
        UpdateStatus(invoice);
        await ledger.ReverseAsync("Charge", "InvoiceItem", item.Id, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Closes the single-line invoice of a cancelled prepaid item. A refunded one stays: the refund already balances it.</summary>
    public async Task ClosePrepaidAsync(InvoiceItem item, CancellationToken ct)
    {
        var invoice = await LockAsync(item.InvoiceId, ct);
        if (invoice.Paid - invoice.Refunded > 0)
        {
            throw new AppException(StatusCodes.Status409Conflict, "line_already_paid", "مبلغ این آیتم پرداخت شده است. ابتدا برگشت وجه ثبت شود.");
        }

        if (invoice.Paid > 0)
        {
            return;
        }

        if (invoice.Discount > 0)
        {
            ledger.Post("DiscountCancelled", "Invoice", invoice.Id, null,
                (Accounts.Receivable, invoice.Discount, 0),
                (Accounts.Discounts, 0, invoice.Discount));
            invoice.Discount = 0;
        }

        await VoidLineAsync(item, ct);
        invoice.Status = InvoiceStatus.Void;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Takes back the charge of a cancelled service: its own invoice closes with it; a line on a shared invoice goes alone.</summary>
    public async Task CancelChargeAsync(string sourceType, long sourceId, CancellationToken ct)
    {
        var charge = await db.InvoiceItems.SingleOrDefaultAsync(i => i.SourceType == sourceType && i.SourceId == sourceId && !i.IsVoid, ct);
        if (charge is null)
        {
            return;
        }

        if (await db.InvoiceItems.AnyAsync(i => i.InvoiceId == charge.InvoiceId && i.Id != charge.Id && !i.IsVoid, ct))
        {
            await VoidLineAsync(charge, ct);
        }
        else
        {
            await ClosePrepaidAsync(charge, ct);
        }
    }

    /// <summary>Must run inside a transaction. A duplicate stops on the invoice lock: the second one finds no balance left.</summary>
    public async Task<Payment> ReceiveAsync(PaymentRequest request, bool fullOnly, CancellationToken ct)
    {
        Money.EnsureCents(request.Amount);
        var userId = user.Id;

        // A shared lock lets payments run side by side, but makes closing the session wait until they commit,
        // so the amount calculated at closing can never miss one.
        var sessions = await db.CashSessions
            .FromSqlInterpolated($"SELECT * FROM CashSessions WHERE OpenedByUserId = {userId} AND ClosedAt IS NULL ORDER BY Id LIMIT 1 FOR SHARE")
            .AsNoTracking().ToListAsync(ct);
        var session = sessions.FirstOrDefault()?.Id
            ?? throw new AppException(StatusCodes.Status409Conflict, "no_open_session", "ابتدا صندوق را باز کنید.");

        var invoice = await LockAsync(request.InvoiceId, ct);
        if (invoice.Status == InvoiceStatus.Void)
        {
            throw new AppException(StatusCodes.Status409Conflict, "invoice_void", "این فاکتور باطل شده است.");
        }

        if (request.InvoiceVersion is { } seen && seen != invoice.Version)
        {
            throw new AppException(StatusCodes.Status409Conflict, "invoice_changed", "این فاکتور همین حالا تغییر کرده یا پرداخت شده است. مانده تازه را ببینید و دوباره تصمیم بگیرید.");
        }

        if (request.Amount > Balance(invoice))
        {
            throw new AppException(StatusCodes.Status409Conflict, "overpayment", "مبلغ پرداخت از مانده فاکتور بیشتر است.");
        }

        if (fullOnly && request.Amount != Balance(invoice))
        {
            throw new AppException(StatusCodes.Status409Conflict, "full_payment_required", "این فیس باید کامل پرداخت شود.");
        }

        var payment = new Payment
        {
            ReceiptNumber = $"RC-{await numbers.NextAsync("receipt", ct: ct):D7}",
            InvoiceId = invoice.Id,
            Amount = request.Amount,
            Method = request.Method,
            Reference = Text.Clean(request.Reference),
            CashSessionId = session,
            ReceivedByUserId = userId,
        };
        db.Payments.Add(payment);
        invoice.Paid += request.Amount;
        UpdateStatus(invoice);
        await db.SaveChangesAsync(ct);

        ledger.Post("Payment", "Payment", payment.Id, payment.ReceiptNumber,
            (Accounts.Cash, payment.Amount, 0),
            (Accounts.Receivable, 0, payment.Amount));
        audit.Add("PaymentReceived", "Payment", payment.Id.ToString());
        outbox.Add("PaymentReceived", new { invoiceId = invoice.Id }, Groups.Cashier, Groups.Finance, Groups.Management, Groups.Reception, Groups.Triage, Groups.Lab);
        await db.SaveChangesAsync(ct);
        return payment;
    }

    public static decimal Balance(Invoice i) => i.Total - i.Discount - i.Paid;

    /// <summary>The payable amount after discount is covered by money still held, so a refund undoes it.</summary>
    public static bool IsSettled(Invoice i) => i.Status != InvoiceStatus.Void && i.Total - i.Discount <= i.Paid - i.Refunded;

    public static void UpdateStatus(Invoice i)
    {
        if (i.Status == InvoiceStatus.Void)
        {
            return;
        }

        i.Status = i.Paid == 0 ? InvoiceStatus.Issued : Balance(i) > 0 ? InvoiceStatus.PartiallyPaid : InvoiceStatus.Paid;
    }
}
