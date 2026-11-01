using System.ComponentModel.DataAnnotations;
using AlShifa.Api.Features.Patients;
using AlShifa.Api.Features.Users;
using AlShifa.Api.Features.Visits;

namespace AlShifa.Api.Features.Finance;

public class Service
{
    public int Id { get; set; }

    [MaxLength(30)]
    public string Code { get; set; } = "";

    [MaxLength(150)]
    public string Name { get; set; } = "";

    public int? DepartmentId { get; set; }

    public Department? Department { get; set; }

    public bool IsActive { get; set; } = true;
}

/// <summary>Prices are append-only by effective date, so changing a tariff never alters past invoices.</summary>
public class ServicePrice
{
    public long Id { get; set; }

    public int ServiceId { get; set; }

    public Service? Service { get; set; }

    public decimal Price { get; set; }

    public DateTime EffectiveFrom { get; set; }

    public long CreatedByUserId { get; set; }
}

public enum InvoiceStatus
{
    Issued,
    PartiallyPaid,
    Paid,
    Void,
}

public class Invoice
{
    public long Id { get; set; }

    [MaxLength(20)]
    public string InvoiceNumber { get; set; } = "";

    public long? PatientId { get; set; }

    public Patient? Patient { get; set; }

    public long? VisitId { get; set; }

    public Visit? Visit { get; set; }

    public InvoiceStatus Status { get; set; } = InvoiceStatus.Issued;

    public decimal Total { get; set; }

    public decimal Discount { get; set; }

    public decimal Paid { get; set; }

    public decimal Refunded { get; set; }

    public long IssuedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public int Version { get; set; }

    public List<InvoiceItem> Items { get; set; } = [];
}

public class InvoiceItem
{
    public long Id { get; set; }

    public long InvoiceId { get; set; }

    public Invoice? Invoice { get; set; }

    [MaxLength(200)]
    public string Description { get; set; } = "";

    public decimal Quantity { get; set; } = 1;

    /// <summary>Copied at issue time; later tariff changes do not touch it.</summary>
    public decimal UnitPrice { get; set; }

    public decimal LineTotal { get; set; }

    public int? ServiceId { get; set; }

    /// <summary>Revenue account credited for this line.</summary>
    [MaxLength(10)]
    public string RevenueAccount { get; set; } = "";

    [MaxLength(30)]
    public string? SourceType { get; set; }

    public long? SourceId { get; set; }

    public bool IsVoid { get; set; }
}

public enum PaymentMethod
{
    Cash,
    Other,
}

public class Payment
{
    public long Id { get; set; }

    [MaxLength(20)]
    public string ReceiptNumber { get; set; } = "";

    public long InvoiceId { get; set; }

    public Invoice? Invoice { get; set; }

    public decimal Amount { get; set; }

    public PaymentMethod Method { get; set; }

    [MaxLength(100)]
    public string? Reference { get; set; }

    public long CashSessionId { get; set; }

    public CashSession? CashSession { get; set; }

    public long ReceivedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public decimal Refunded { get; set; }
}

public class Refund
{
    public long Id { get; set; }

    [MaxLength(20)]
    public string RefundNumber { get; set; } = "";

    public long PaymentId { get; set; }

    public Payment? Payment { get; set; }

    public decimal Amount { get; set; }

    [MaxLength(300)]
    public string Reason { get; set; } = "";

    public long ApprovedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class CashRegister
{
    public int Id { get; set; }

    [MaxLength(100)]
    public string Name { get; set; } = "";

    public bool IsActive { get; set; } = true;
}

public class CashSession
{
    public long Id { get; set; }

    public int RegisterId { get; set; }

    public CashRegister? Register { get; set; }

    public long OpenedByUserId { get; set; }

    public decimal OpeningAmount { get; set; }

    public DateTime OpenedAt { get; set; } = DateTime.UtcNow;

    public long? ClosedByUserId { get; set; }

    public DateTime? ClosedAt { get; set; }

    public decimal? ExpectedAmount { get; set; }

    public decimal? CountedAmount { get; set; }

    public int Version { get; set; }
}

public class LedgerAccount
{
    [Key]
    [MaxLength(10)]
    public string Code { get; set; } = "";

    [MaxLength(100)]
    public string Name { get; set; } = "";
}

public class LedgerTransaction
{
    public long Id { get; set; }

    [MaxLength(30)]
    public string Kind { get; set; } = "";

    [MaxLength(30)]
    public string SourceType { get; set; } = "";

    public long SourceId { get; set; }

    [MaxLength(200)]
    public string? Description { get; set; }

    public long? ReversalOfId { get; set; }

    public long CreatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<LedgerEntry> Entries { get; set; } = [];
}

public class LedgerEntry
{
    public long Id { get; set; }

    public long TransactionId { get; set; }

    [MaxLength(10)]
    public string AccountCode { get; set; } = "";

    public LedgerAccount? Account { get; set; }

    public decimal Debit { get; set; }

    public decimal Credit { get; set; }
}

public enum ExpenseStatus
{
    Recorded,
    Cancelled,
}

public class Expense
{
    public long Id { get; set; }

    [MaxLength(20)]
    public string ExpenseNumber { get; set; } = "";

    [MaxLength(100)]
    public string Category { get; set; } = "";

    [MaxLength(300)]
    public string? Description { get; set; }

    public decimal Amount { get; set; }

    public DateOnly ExpenseDate { get; set; }

    public ExpenseStatus Status { get; set; } = ExpenseStatus.Recorded;

    public long CreatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public int Version { get; set; }
}
