using System.ComponentModel.DataAnnotations;
using AlShifa.Api.Features.Clinical;
using AlShifa.Api.Features.Finance;
using AlShifa.Api.Features.Patients;

namespace AlShifa.Api.Features.Inventory;

public enum ProductKind
{
    Drug,
    Frame,
    Lens,
    General,
}

public class Product
{
    public int Id { get; set; }

    [MaxLength(40)]
    public string Code { get; set; } = "";

    [MaxLength(200)]
    public string Name { get; set; } = "";

    public ProductKind Kind { get; set; }

    [MaxLength(80)]
    public string? Category { get; set; }

    [MaxLength(30)]
    public string Unit { get; set; } = "";

    public decimal ReorderLevel { get; set; }

    public decimal SalePrice { get; set; }

    public bool IsActive { get; set; } = true;

    public int Version { get; set; }
}

public class StockLocation
{
    public int Id { get; set; }

    [MaxLength(30)]
    public string Code { get; set; } = "";

    [MaxLength(100)]
    public string Name { get; set; } = "";
}

public class StockBatch
{
    public long Id { get; set; }

    public int ProductId { get; set; }

    public Product? Product { get; set; }

    public int LocationId { get; set; }

    public StockLocation? Location { get; set; }

    [MaxLength(50)]
    public string LotNumber { get; set; } = "";

    public DateOnly? ExpiryDate { get; set; }

    public decimal Quantity { get; set; }

    public decimal UnitCost { get; set; }
}

public enum MovementKind
{
    Purchase,
    Sale,
    SaleReturn,
    Adjustment,
    Consumption,
    TransferOut,
    TransferIn,
}

/// <summary>Append-only stock history. Every row names the document that caused it.</summary>
public class StockMovement
{
    public long Id { get; set; }

    public long BatchId { get; set; }

    public StockBatch? Batch { get; set; }

    public MovementKind Kind { get; set; }

    public decimal Quantity { get; set; }

    [MaxLength(30)]
    public string SourceType { get; set; } = "";

    public long SourceId { get; set; }

    [MaxLength(300)]
    public string? Reason { get; set; }

    public long UserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Purchase
{
    public long Id { get; set; }

    [MaxLength(20)]
    public string PurchaseNumber { get; set; } = "";

    [MaxLength(150)]
    public string? SupplierName { get; set; }

    public int LocationId { get; set; }

    public StockLocation? Location { get; set; }

    public decimal Total { get; set; }

    public long CreatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<PurchaseItem> Items { get; set; } = [];
}

public class PurchaseItem
{
    public long Id { get; set; }

    public long PurchaseId { get; set; }

    public int ProductId { get; set; }

    public Product? Product { get; set; }

    public long BatchId { get; set; }

    public decimal Quantity { get; set; }

    public decimal UnitCost { get; set; }
}

public enum SaleKind
{
    Pharmacy,
    Optical,
}

public class Sale
{
    public long Id { get; set; }

    [MaxLength(20)]
    public string SaleNumber { get; set; } = "";

    public SaleKind Kind { get; set; }

    public long? PatientId { get; set; }

    public Patient? Patient { get; set; }

    public long? PrescriptionId { get; set; }

    public MedicalPrescription? Prescription { get; set; }

    public long InvoiceId { get; set; }

    public Invoice? Invoice { get; set; }

    public bool IsReturned { get; set; }

    public long CreatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public int Version { get; set; }

    public List<SaleItem> Items { get; set; } = [];
}

public class SaleItem
{
    public long Id { get; set; }

    public long SaleId { get; set; }

    public int ProductId { get; set; }

    public Product? Product { get; set; }

    public long BatchId { get; set; }

    public long? PrescriptionItemId { get; set; }

    public decimal Quantity { get; set; }

    public decimal UnitPrice { get; set; }

    public decimal UnitCost { get; set; }
}

public enum OpticalOrderStatus
{
    Ordered,
    InWorkshop,
    Ready,
    Delivered,
    Cancelled,
}

public class OpticalOrder
{
    public long Id { get; set; }

    [MaxLength(20)]
    public string OrderNumber { get; set; } = "";

    public long? PatientId { get; set; }

    public Patient? Patient { get; set; }

    public long? OpticalPrescriptionId { get; set; }

    public OpticalPrescription? OpticalPrescription { get; set; }

    [MaxLength(500)]
    public string? Description { get; set; }

    public long SaleId { get; set; }

    public Sale? Sale { get; set; }

    public OpticalOrderStatus Status { get; set; } = OpticalOrderStatus.Ordered;

    public DateTime? DeliveredAt { get; set; }

    public long CreatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public int Version { get; set; }
}
