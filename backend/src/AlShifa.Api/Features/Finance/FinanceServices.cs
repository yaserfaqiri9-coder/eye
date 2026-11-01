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

public sealed record ChargeLine(string Description, decimal Quantity, decimal UnitPrice, string RevenueAccount, int? ServiceId, string? SourceType, long? SourceId);

public sealed class InvoiceService(AppDbContext db, NumberService numbers, LedgerService ledger, CurrentUser user, Outbox outbox)
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
            .Where(i => i.VisitId == visitId && i.Status != InvoiceStatus.Void)
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
        outbox.Add("InvoiceIssued", new { invoiceId = invoice.Id }, Groups.Cashier, Groups.Finance);
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

    public static decimal Balance(Invoice i) => i.Total - i.Discount - i.Paid;

    public static void UpdateStatus(Invoice i)
    {
        if (i.Status == InvoiceStatus.Void)
        {
            return;
        }

        i.Status = i.Paid == 0 ? InvoiceStatus.Issued : Balance(i) > 0 ? InvoiceStatus.PartiallyPaid : InvoiceStatus.Paid;
    }
}
