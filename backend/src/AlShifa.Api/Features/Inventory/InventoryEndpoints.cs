using System.ComponentModel.DataAnnotations;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Clinical;
using AlShifa.Api.Features.Finance;
using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AlShifa.Api.Features.Inventory;

public sealed record SaveProductRequest(
    [Required, MaxLength(40)] string Code,
    [Required, MaxLength(200)] string Name,
    ProductKind Kind,
    [MaxLength(80)] string? Category,
    [Required, MaxLength(30)] string Unit,
    [Range(0, 100000000)] decimal ReorderLevel,
    [Range(0, 100000000)] decimal SalePrice,
    bool IsActive = true,
    int? Version = null);

public sealed record ProductRow(int Id, string Code, string Name, string Kind, string? Category, string Unit, decimal ReorderLevel, decimal SalePrice, bool IsActive, decimal OnHand, int Version);

public sealed record BatchRow(long Id, int ProductId, string ProductName, string LocationCode, string LocationName, string LotNumber, DateOnly? ExpiryDate, decimal Quantity, decimal UnitCost);

public sealed record PurchaseLineInput(int ProductId, [MaxLength(50)] string? LotNumber, DateOnly? ExpiryDate, [Range(0.001, 100000000)] decimal Quantity, [Range(0, 100000000)] decimal UnitCost);

public sealed record CreatePurchaseRequest([MaxLength(150)] string? SupplierName, [Required, MaxLength(30)] string LocationCode, [Required, MinLength(1)] PurchaseLineInput[] Items);

public sealed record AdjustStockRequest(long BatchId, decimal Quantity, MovementKind Kind, [Required, MinLength(3), MaxLength(300)] string Reason);

public sealed record TransferStockRequest(long BatchId, [Required, MaxLength(30)] string ToLocationCode, [Range(0.001, 100000000)] decimal Quantity);

public sealed record MovementRow(long Id, string ProductName, string LocationName, string LotNumber, string Kind, decimal Quantity, string SourceType, long SourceId, string? Reason, DateTime CreatedAt);

public sealed record CreateSaleRequest(long? PatientId, long? PrescriptionId, [Required, MinLength(1)] SaleLineInput[] Items);

public sealed record ReturnSaleRequest(int Version, [Required, MinLength(3), MaxLength(300)] string Reason);

public sealed record SaleRow(long Id, string SaleNumber, string Kind, long? PatientId, string? PatientName, long? PrescriptionId, long InvoiceId, string InvoiceNumber, decimal Total, decimal Balance, string InvoiceStatus, bool IsReturned, DateTime CreatedAt, int Version);

public sealed record CreateOpticalOrderRequest(long? PatientId, long? OpticalPrescriptionId, [MaxLength(500)] string? Description, SaleLineInput[]? Items, CustomLineInput[]? CustomLines);

public sealed record ChangeOpticalStatusRequest(OpticalOrderStatus Status, int Version);

public sealed record OpticalOrderRow(long Id, string OrderNumber, long? PatientId, string? PatientName, long? OpticalPrescriptionId, string? Description, string Status, long InvoiceId, string InvoiceNumber, decimal Total, decimal Balance, DateTime? DeliveredAt, DateTime CreatedAt, int Version, IReadOnlyList<string> NextStatuses);

public static class InventoryEndpoints
{
    private static readonly Dictionary<OpticalOrderStatus, OpticalOrderStatus[]> OpticalFlow = new()
    {
        [OpticalOrderStatus.Ordered] = [OpticalOrderStatus.InWorkshop, OpticalOrderStatus.Ready, OpticalOrderStatus.Delivered, OpticalOrderStatus.Cancelled],
        [OpticalOrderStatus.InWorkshop] = [OpticalOrderStatus.Ready, OpticalOrderStatus.Cancelled],
        [OpticalOrderStatus.Ready] = [OpticalOrderStatus.Delivered, OpticalOrderStatus.Cancelled],
        [OpticalOrderStatus.Delivered] = [],
        [OpticalOrderStatus.Cancelled] = [],
    };

    public static void MapInventory(this IEndpointRouteBuilder app)
    {
        var inventory = app.MapGroup("/api/inventory").WithTags("Inventory");

        inventory.MapGet("/products", async (AppDbContext db, string? q, ProductKind? kind, int page = 1, int pageSize = 50, CancellationToken ct = default) =>
        {
            pageSize = Math.Clamp(pageSize, 1, 100);
            page = Math.Max(page, 1);

            var query = db.Products.AsNoTracking();
            if (kind is { } k)
            {
                query = query.Where(p => p.Kind == k);
            }

            if (!string.IsNullOrWhiteSpace(q))
            {
                query = query.Where(p => p.Code.StartsWith(q) || p.Name.StartsWith(q));
            }

            var total = await query.CountAsync(ct);
            var rows = await ProjectProducts(db, query.OrderBy(p => p.Name).Skip((page - 1) * pageSize).Take(pageSize)).ToListAsync(ct);
            return Results.Ok(new PagedResult<ProductRow>(rows, total, page, pageSize));
        }).RequirePermission("inventory.view");

        inventory.MapPost("/products", async (SaveProductRequest request, AppDbContext db, AuditService audit, CancellationToken ct) =>
        {
            var product = new Product();
            Apply(product, request);
            db.Products.Add(product);
            await db.SaveChangesAsync(ct);
            audit.Add("ProductCreated", nameof(Product), product.Id.ToString());
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/inventory/products/{product.Id}", new { product.Id });
        }).RequirePermission("inventory.create");

        inventory.MapPut("/products/{id:int}", async (int id, SaveProductRequest request, AppDbContext db, AuditService audit, CancellationToken ct) =>
        {
            if (request.Version is null)
            {
                throw new AppException(StatusCodes.Status400BadRequest, "version_required", "نسخه رکورد ارسال نشده است.");
            }

            var product = await db.Products.SingleOrDefaultAsync(p => p.Id == id, ct) ?? throw AppException.NotFound();
            db.Entry(product).Property(p => p.Version).OriginalValue = request.Version.Value;
            Apply(product, request);
            audit.Add("ProductEdited", nameof(Product), id.ToString());
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequirePermission("inventory.edit");

        inventory.MapGet("/batches", async (AppDbContext db, int? productId, string? location, CancellationToken ct) =>
        {
            var query = db.StockBatches.AsNoTracking().Where(b => b.Quantity > 0);
            if (productId is { } p)
            {
                query = query.Where(b => b.ProductId == p);
            }

            if (!string.IsNullOrWhiteSpace(location))
            {
                query = query.Where(b => b.Location!.Code == location);
            }

            return Results.Ok(await ProjectBatches(query.OrderBy(b => b.Product!.Name).ThenBy(b => b.ExpiryDate).Take(300)).ToListAsync(ct));
        }).RequirePermission("inventory.view");

        inventory.MapGet("/alerts", async (AppDbContext db, CancellationToken ct) =>
        {
            var soon = KabulClock.Today.AddDays(90);
            var expiring = await ProjectBatches(db.StockBatches.AsNoTracking().Where(b => b.Quantity > 0 && b.ExpiryDate != null && b.ExpiryDate <= soon).OrderBy(b => b.ExpiryDate).Take(100)).ToListAsync(ct);
            var low = await ProjectProducts(db, db.Products.AsNoTracking().Where(p => p.IsActive && p.ReorderLevel > 0 && (db.StockBatches.Where(b => b.ProductId == p.Id).Sum(b => (decimal?)b.Quantity) ?? 0) <= p.ReorderLevel).OrderBy(p => p.Name).Take(100)).ToListAsync(ct);
            return Results.Ok(new { lowStock = low, expiring });
        }).RequirePermission("inventory.view");

        inventory.MapGet("/movements", async (AppDbContext db, int? productId, int page = 1, int pageSize = 50, CancellationToken ct = default) =>
        {
            pageSize = Math.Clamp(pageSize, 1, 100);
            page = Math.Max(page, 1);

            var query = db.StockMovements.AsNoTracking();
            if (productId is { } p)
            {
                query = query.Where(m => m.Batch!.ProductId == p);
            }

            var total = await query.CountAsync(ct);
            var rows = await query.OrderByDescending(m => m.Id).Skip((page - 1) * pageSize).Take(pageSize)
                .Select(m => new { m.Id, m.Batch!.Product!.Name, Location = m.Batch.Location!.Name, m.Batch.LotNumber, m.Kind, m.Quantity, m.SourceType, m.SourceId, m.Reason, m.CreatedAt })
                .ToListAsync(ct);
            var items = rows.Select(m => new MovementRow(m.Id, m.Name, m.Location, m.LotNumber, m.Kind.ToString(), m.Quantity, m.SourceType, m.SourceId, m.Reason, m.CreatedAt)).ToList();
            return Results.Ok(new PagedResult<MovementRow>(items, total, page, pageSize));
        }).RequirePermission("inventory.view");

        inventory.MapPost("/purchases", async (CreatePurchaseRequest request, AppDbContext db, Idempotency idempotency, NumberService numbers, StockService stock, LedgerService ledger, CurrentUser user, AuditService audit, Outbox outbox, CancellationToken ct) =>
            await idempotency.RunAsync(request, async () =>
            {
                var locationId = await db.StockLocations.Where(l => l.Code == request.LocationCode).Select(l => (int?)l.Id).SingleOrDefaultAsync(ct)
                    ?? throw new AppException(StatusCodes.Status400BadRequest, "unknown_location", "محل نگهداری معتبر نیست.");

                var purchase = new Purchase
                {
                    PurchaseNumber = $"PU-{await numbers.NextAsync("purchase", ct: ct):D6}",
                    SupplierName = Text.Clean(request.SupplierName),
                    LocationId = locationId,
                    CreatedByUserId = user.Id,
                };
                db.Purchases.Add(purchase);
                await db.SaveChangesAsync(ct);

                foreach (var line in request.Items.OrderBy(l => l.ProductId))
                {
                    if (!await db.Products.AnyAsync(p => p.Id == line.ProductId, ct))
                    {
                        throw new AppException(StatusCodes.Status400BadRequest, "unknown_product", "جنس انتخاب‌شده معتبر نیست.");
                    }

                    var batch = await FindOrCreateBatchAsync(db, line.ProductId, locationId, line.LotNumber ?? "", line.ExpiryDate, line.UnitCost, ct);
                    batch.Quantity += line.Quantity;
                    stock.AddMovement(batch, MovementKind.Purchase, line.Quantity, nameof(Purchase), purchase.Id);
                    await db.SaveChangesAsync(ct);

                    purchase.Items.Add(new PurchaseItem { ProductId = line.ProductId, BatchId = batch.Id, Quantity = line.Quantity, UnitCost = line.UnitCost });
                    purchase.Total += decimal.Round(line.Quantity * line.UnitCost, 2, MidpointRounding.AwayFromZero);
                }

                ledger.Post("Purchase", nameof(Purchase), purchase.Id, purchase.PurchaseNumber,
                    (Accounts.Inventory, purchase.Total, 0),
                    (Accounts.Payable, 0, purchase.Total));
                audit.Add("PurchaseRecorded", nameof(Purchase), purchase.Id.ToString());
                outbox.Add("InventoryChanged", new { purchaseId = purchase.Id }, Groups.Pharmacy, Groups.Optical, Groups.Inventory);
                await db.SaveChangesAsync(ct);
                return (StatusCodes.Status201Created, new { purchase.Id, purchase.PurchaseNumber, purchase.Total });
            }, ct)).RequirePermission("inventory.create");

        inventory.MapPost("/adjustments", async (AdjustStockRequest request, AppDbContext db, Idempotency idempotency, StockService stock, LedgerService ledger, AuditService audit, Outbox outbox, CancellationToken ct) =>
            await idempotency.RunAsync(request, async () =>
            {
                if (request.Kind is not (MovementKind.Adjustment or MovementKind.Consumption) || request.Quantity == 0)
                {
                    throw new AppException(StatusCodes.Status400BadRequest, "invalid_adjustment", "نوع یا مقدار تعدیل معتبر نیست.");
                }

                if (request.Kind == MovementKind.Consumption && request.Quantity > 0)
                {
                    throw new AppException(StatusCodes.Status400BadRequest, "invalid_adjustment", "مصرف باید مقدار منفی باشد.");
                }

                var batch = await db.StockBatches.FromSqlInterpolated($"SELECT * FROM StockBatches WHERE Id = {request.BatchId} FOR UPDATE").SingleOrDefaultAsync(ct) ?? throw AppException.NotFound();
                if (batch.Quantity + request.Quantity < 0)
                {
                    throw new AppException(StatusCodes.Status409Conflict, "insufficient_stock", "موجودی این بچ کافی نیست.");
                }

                batch.Quantity += request.Quantity;
                stock.AddMovement(batch, request.Kind, request.Quantity, "Adjustment", batch.Id, request.Reason.Trim());
                await db.SaveChangesAsync(ct);

                var movementId = await db.StockMovements.Where(m => m.BatchId == batch.Id).MaxAsync(m => m.Id, ct);
                var value = decimal.Round(Math.Abs(request.Quantity) * batch.UnitCost, 2, MidpointRounding.AwayFromZero);
                if (request.Quantity < 0)
                {
                    ledger.Post("Adjustment", "StockMovement", movementId, request.Reason, (Accounts.InventoryAdjustments, value, 0), (Accounts.Inventory, 0, value));
                }
                else
                {
                    ledger.Post("Adjustment", "StockMovement", movementId, request.Reason, (Accounts.Inventory, value, 0), (Accounts.InventoryAdjustments, 0, value));
                }

                audit.Add("StockAdjusted", nameof(StockBatch), batch.Id.ToString(), request.Reason);
                outbox.Add("InventoryChanged", new { batchId = batch.Id }, Groups.Pharmacy, Groups.Optical, Groups.Inventory);
                await db.SaveChangesAsync(ct);
                return (StatusCodes.Status201Created, new { batch.Id, batch.Quantity });
            }, ct)).RequirePermission("inventory.approve");

        inventory.MapPost("/transfers", async (TransferStockRequest request, AppDbContext db, Idempotency idempotency, StockService stock, AuditService audit, Outbox outbox, CancellationToken ct) =>
            await idempotency.RunAsync(request, async () =>
            {
                var toLocationId = await db.StockLocations.Where(l => l.Code == request.ToLocationCode).Select(l => (int?)l.Id).SingleOrDefaultAsync(ct)
                    ?? throw new AppException(StatusCodes.Status400BadRequest, "unknown_location", "محل نگهداری معتبر نیست.");

                var source = await db.StockBatches.FromSqlInterpolated($"SELECT * FROM StockBatches WHERE Id = {request.BatchId} FOR UPDATE").SingleOrDefaultAsync(ct) ?? throw AppException.NotFound();
                if (source.LocationId == toLocationId)
                {
                    throw new AppException(StatusCodes.Status400BadRequest, "same_location", "مبدأ و مقصد یکی است.");
                }

                if (source.Quantity < request.Quantity)
                {
                    throw new AppException(StatusCodes.Status409Conflict, "insufficient_stock", "موجودی این بچ کافی نیست.");
                }

                var target = await FindOrCreateBatchAsync(db, source.ProductId, toLocationId, source.LotNumber, source.ExpiryDate, source.UnitCost, ct);
                source.Quantity -= request.Quantity;
                target.Quantity += request.Quantity;
                stock.AddMovement(source, MovementKind.TransferOut, -request.Quantity, "Transfer", source.Id);
                stock.AddMovement(target, MovementKind.TransferIn, request.Quantity, "Transfer", source.Id);
                audit.Add("StockTransferred", nameof(StockBatch), source.Id.ToString(), request.ToLocationCode);
                outbox.Add("InventoryChanged", new { batchId = source.Id }, Groups.Pharmacy, Groups.Optical, Groups.Inventory);
                await db.SaveChangesAsync(ct);
                return (StatusCodes.Status201Created, new { sourceId = source.Id, targetId = target.Id });
            }, ct)).RequirePermission("inventory.edit");

        var pharmacy = app.MapGroup("/api/pharmacy").WithTags("Pharmacy");

        pharmacy.MapGet("/sales", async (AppDbContext db, int page = 1, int pageSize = 50, CancellationToken ct = default) =>
        {
            pageSize = Math.Clamp(pageSize, 1, 100);
            page = Math.Max(page, 1);
            var query = db.Sales.AsNoTracking().Where(s => s.Kind == SaleKind.Pharmacy);
            var total = await query.CountAsync(ct);
            var rows = await ProjectSales(query.OrderByDescending(s => s.Id).Skip((page - 1) * pageSize).Take(pageSize)).ToListAsync(ct);
            return Results.Ok(new PagedResult<SaleRow>(rows, total, page, pageSize));
        }).RequirePermission("pharmacy.view");

        pharmacy.MapPost("/sales", async (CreateSaleRequest request, AppDbContext db, Idempotency idempotency, SaleService sales, CancellationToken ct) =>
            await idempotency.RunAsync(request, async () =>
            {
                var sale = await sales.CreateAsync(SaleKind.Pharmacy, request.PatientId, request.PrescriptionId, request.Items, [], ct);
                return (StatusCodes.Status201Created, await ProjectSales(db.Sales.AsNoTracking().Where(s => s.Id == sale.Id)).SingleAsync(ct));
            }, ct)).RequirePermission("pharmacy.create");

        pharmacy.MapPost("/sales/{id:long}/return", async (long id, ReturnSaleRequest request, AppDbContext db, SaleService sales, CancellationToken ct) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await sales.ReturnAsync(id, request.Version, request.Reason.Trim(), ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        }).RequirePermission("pharmacy.cancel");

        var optical = app.MapGroup("/api/optical").WithTags("Optical");

        optical.MapGet("/orders", async (AppDbContext db, OpticalOrderStatus? status, int page = 1, int pageSize = 50, CancellationToken ct = default) =>
        {
            pageSize = Math.Clamp(pageSize, 1, 100);
            page = Math.Max(page, 1);
            var query = db.OpticalOrders.AsNoTracking();
            if (status is { } s)
            {
                query = query.Where(o => o.Status == s);
            }

            var total = await query.CountAsync(ct);
            var rows = await ProjectOrders(query.OrderByDescending(o => o.Id).Skip((page - 1) * pageSize).Take(pageSize)).ToListAsync(ct);
            return Results.Ok(new PagedResult<OpticalOrderRow>(rows.Select(ToRow).ToList(), total, page, pageSize));
        }).RequirePermission("optical.view");

        optical.MapPost("/orders", async (CreateOpticalOrderRequest request, AppDbContext db, Idempotency idempotency, SaleService sales, NumberService numbers, CurrentUser user, AuditService audit, Outbox outbox, CancellationToken ct) =>
            await idempotency.RunAsync(request, async () =>
            {
                if (request.OpticalPrescriptionId is { } rxId)
                {
                    var owner = await db.OpticalPrescriptions.Where(p => p.Id == rxId && !p.IsCancelled).Select(p => (long?)p.PatientId).SingleOrDefaultAsync(ct);
                    if (owner is null || owner != request.PatientId)
                    {
                        throw new AppException(StatusCodes.Status400BadRequest, "prescription_mismatch", "این نسخه به این مریض تعلق ندارد.");
                    }
                }

                var sale = await sales.CreateAsync(SaleKind.Optical, request.PatientId, null, request.Items ?? [], request.CustomLines ?? [], ct);
                var order = new OpticalOrder
                {
                    OrderNumber = $"OO-{await numbers.NextAsync("optical-order", ct: ct):D6}",
                    PatientId = request.PatientId,
                    OpticalPrescriptionId = request.OpticalPrescriptionId,
                    Description = Text.Clean(request.Description),
                    SaleId = sale.Id,
                    CreatedByUserId = user.Id,
                };
                db.OpticalOrders.Add(order);
                await db.SaveChangesAsync(ct);
                audit.Add("OpticalOrderCreated", nameof(OpticalOrder), order.Id.ToString());
                outbox.Add("OpticalOrderChanged", new { orderId = order.Id }, Groups.Optical, Groups.Cashier);
                await db.SaveChangesAsync(ct);
                var row = await ProjectOrders(db.OpticalOrders.AsNoTracking().Where(o => o.Id == order.Id)).SingleAsync(ct);
                return (StatusCodes.Status201Created, ToRow(row));
            }, ct)).RequirePermission("optical.create");

        optical.MapPost("/orders/{id:long}/status", async (long id, ChangeOpticalStatusRequest request, AppDbContext db, SaleService sales, AuditService audit, Outbox outbox, HttpContext http, CancellationToken ct) =>
        {
            var needed = request.Status == OpticalOrderStatus.Cancelled ? "optical.cancel" : "optical.edit";
            if (!http.User.HasClaim(RolePermissions.ClaimType, needed))
            {
                throw new AppException(StatusCodes.Status403Forbidden, "forbidden", "شما اجازه این عملیات را ندارید.");
            }

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var order = await db.OpticalOrders.SingleOrDefaultAsync(o => o.Id == id, ct) ?? throw AppException.NotFound();
            db.Entry(order).Property(o => o.Version).OriginalValue = request.Version;
            if (!OpticalFlow[order.Status].Contains(request.Status))
            {
                throw new AppException(StatusCodes.Status409Conflict, "invalid_transition", "این تغییر وضعیت مجاز نیست.");
            }

            if (request.Status == OpticalOrderStatus.Cancelled)
            {
                var saleVersion = await db.Sales.Where(s => s.Id == order.SaleId).Select(s => s.Version).SingleAsync(ct);
                await sales.ReturnAsync(order.SaleId, saleVersion, "لغو سفارش عینک", ct);
            }

            order.Status = request.Status;
            order.DeliveredAt = request.Status == OpticalOrderStatus.Delivered ? DateTime.UtcNow : order.DeliveredAt;
            audit.Add("OpticalOrderStatusChanged", nameof(OpticalOrder), id.ToString(), request.Status.ToString());
            outbox.Add("OpticalOrderChanged", new { orderId = id }, Groups.Optical, Groups.Cashier);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization();
    }

    private static async Task<StockBatch> FindOrCreateBatchAsync(AppDbContext db, int productId, int locationId, string lot, DateOnly? expiry, decimal unitCost, CancellationToken ct)
    {
        var expiryValue = expiry?.ToDateTime(TimeOnly.MinValue);
        var existing = await db.StockBatches.FromSqlInterpolated($"""
            SELECT * FROM StockBatches
            WHERE ProductId = {productId} AND LocationId = {locationId} AND LotNumber = {lot} AND (ExpiryDate <=> {expiryValue}) AND UnitCost = {unitCost}
            FOR UPDATE
            """).SingleOrDefaultAsync(ct);
        if (existing is not null)
        {
            return existing;
        }

        var batch = new StockBatch { ProductId = productId, LocationId = locationId, LotNumber = lot, ExpiryDate = expiry, UnitCost = unitCost };
        db.StockBatches.Add(batch);
        await db.SaveChangesAsync(ct);
        return batch;
    }

    private static void Apply(Product p, SaveProductRequest r)
    {
        p.Code = r.Code.Trim();
        p.Name = r.Name.Trim();
        p.Kind = r.Kind;
        p.Category = Text.Clean(r.Category);
        p.Unit = r.Unit.Trim();
        p.ReorderLevel = r.ReorderLevel;
        p.SalePrice = r.SalePrice;
        p.IsActive = r.IsActive;
    }

    private static IQueryable<ProductRow> ProjectProducts(AppDbContext db, IQueryable<Product> products) => products.Select(p => new ProductRow(
        p.Id, p.Code, p.Name, p.Kind.ToString(), p.Category, p.Unit, p.ReorderLevel, p.SalePrice, p.IsActive,
        db.StockBatches.Where(b => b.ProductId == p.Id).Sum(b => (decimal?)b.Quantity) ?? 0, p.Version));

    private static IQueryable<BatchRow> ProjectBatches(IQueryable<StockBatch> batches) => batches.Select(b => new BatchRow(
        b.Id, b.ProductId, b.Product!.Name, b.Location!.Code, b.Location.Name, b.LotNumber, b.ExpiryDate, b.Quantity, b.UnitCost));

    private static IQueryable<SaleRow> ProjectSales(IQueryable<Sale> sales) => sales.Select(s => new SaleRow(
        s.Id, s.SaleNumber, s.Kind.ToString(), s.PatientId, s.Patient != null ? (s.Patient.FirstName + " " + (s.Patient.LastName ?? "")).Trim() : null, s.PrescriptionId,
        s.InvoiceId, s.Invoice!.InvoiceNumber, s.Invoice.Total, s.Invoice.Total - s.Invoice.Discount - s.Invoice.Paid, s.Invoice.Status.ToString(), s.IsReturned, s.CreatedAt, s.Version));

    private sealed record OrderProjection(long Id, string OrderNumber, long? PatientId, string? PatientName, long? OpticalPrescriptionId, string? Description, OpticalOrderStatus Status, long InvoiceId, string InvoiceNumber, decimal Total, decimal Balance, DateTime? DeliveredAt, DateTime CreatedAt, int Version);

    private static IQueryable<OrderProjection> ProjectOrders(IQueryable<OpticalOrder> orders) => orders.Select(o => new OrderProjection(
        o.Id, o.OrderNumber, o.PatientId, o.Patient != null ? (o.Patient.FirstName + " " + (o.Patient.LastName ?? "")).Trim() : null, o.OpticalPrescriptionId, o.Description, o.Status,
        o.Sale!.InvoiceId, o.Sale.Invoice!.InvoiceNumber, o.Sale.Invoice.Total, o.Sale.Invoice.Total - o.Sale.Invoice.Discount - o.Sale.Invoice.Paid, o.DeliveredAt, o.CreatedAt, o.Version));

    private static OpticalOrderRow ToRow(OrderProjection o) => new(
        o.Id, o.OrderNumber, o.PatientId, o.PatientName, o.OpticalPrescriptionId, o.Description, o.Status.ToString(), o.InvoiceId, o.InvoiceNumber, o.Total, o.Balance, o.DeliveredAt, o.CreatedAt, o.Version,
        OpticalFlow[o.Status].Select(s => s.ToString()).ToList());
}
