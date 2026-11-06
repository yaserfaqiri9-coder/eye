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

public sealed record CreatePurchaseRequest([MaxLength(150)] string? SupplierName, [Required, MaxLength(30)] string LocationCode, [Required, MinLength(1)] PurchaseLineInput[] Items, int? SupplierId = null);

public sealed record AdjustStockRequest(long BatchId, decimal Quantity, MovementKind Kind, [Required, MinLength(3), MaxLength(300)] string Reason);

public sealed record TransferStockRequest(long BatchId, [Required, MaxLength(30)] string ToLocationCode, [Range(0.001, 100000000)] decimal Quantity);

public sealed record LocationSummary(string Code, string Name, int ProductCount, int BatchCount, decimal StockValue, DateTime? LastMovementAt);

public sealed record LocationStockRow(int ProductId, string ProductCode, string ProductName, string Unit, decimal Quantity, decimal Value, DateOnly? NearestExpiry, int BatchCount);

public sealed record LocationDetail(LocationSummary Location, List<LocationStockRow> Stock);

public sealed record MovementRow(long Id, string ProductName, string LocationName, string LotNumber, string Kind, decimal Quantity, string SourceType, long SourceId, string? Reason, DateTime CreatedAt, bool Reversible = false);

public sealed record ReverseMovementRequest([Required, MinLength(3), MaxLength(300)] string Reason);

public sealed record PurchaseRow(long Id, string PurchaseNumber, int? SupplierId, string? SupplierName, string LocationCode, string LocationName, decimal Total, bool IsCancelled, string? CancelReason, DateTime CreatedAt, int Version);

public sealed record PurchaseLineRow(int ProductId, string ProductName, string LotNumber, DateOnly? ExpiryDate, decimal Quantity, decimal UnitCost);

public sealed record PurchaseDetail(PurchaseRow Purchase, IReadOnlyList<PurchaseLineRow> Items);

public sealed record CancelPurchaseRequest([Required, MinLength(3), MaxLength(300)] string Reason, int Version);

public sealed record CorrectPurchaseRequest(int? SupplierId, [Required, MaxLength(30)] string LocationCode, [Required, MinLength(1)] PurchaseLineInput[] Items, [Required, MinLength(3), MaxLength(300)] string Reason, int Version);

public sealed record SaleLineRow(int ProductId, string ProductName, string Kind, string Unit, decimal SalePrice, decimal OnHand, bool IsActive, decimal Quantity, long? PrescriptionItemId);

public sealed record CustomLineRow(string Description, decimal Amount);

public sealed record SaleDetail(long Id, string SaleNumber, long? PatientId, string? PatientName, long? PrescriptionId, bool IsReturned, int Version, IReadOnlyList<SaleLineRow> Items, IReadOnlyList<CustomLineRow> CustomLines);

public sealed record CorrectSaleRequest([Required, MinLength(1)] SaleLineInput[] Items, [Required, MinLength(3), MaxLength(300)] string Reason, int Version);

public sealed record OpticalOrderDetail(long Id, string OrderNumber, string? Description, long? OpticalPrescriptionId, string Status, int Version, SaleDetail Sale);

public sealed record CorrectOpticalOrderRequest([MaxLength(500)] string? Description, SaleLineInput[]? Items, CustomLineInput[]? CustomLines, [Required, MinLength(3), MaxLength(300)] string Reason, int Version);

public sealed record CreateSaleRequest(long? PatientId, long? PrescriptionId, [Required, MinLength(1)] SaleLineInput[] Items);

public sealed record ReturnSaleRequest(int Version, [Required, MinLength(3), MaxLength(300)] string Reason);

public sealed record SaleRow(long Id, string SaleNumber, string Kind, long? PatientId, string? PatientName, long? PrescriptionId, long InvoiceId, string InvoiceNumber, decimal Total, decimal Balance, string InvoiceStatus, bool IsReturned, DateTime CreatedAt, int Version);

public sealed record CreateOpticalOrderRequest(long? PatientId, long? OpticalPrescriptionId, [MaxLength(500)] string? Description, SaleLineInput[]? Items, CustomLineInput[]? CustomLines);

public sealed record ChangeOpticalStatusRequest(OpticalOrderStatus Status, int Version, [MaxLength(300)] string? Reason = null);

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

        inventory.MapDelete("/products/{id:int}", async (int id, int version, AppDbContext db, AuditService audit, CancellationToken ct) =>
        {
            var product = await db.Products.SingleOrDefaultAsync(p => p.Id == id, ct) ?? throw AppException.NotFound();
            db.Entry(product).Property(p => p.Version).OriginalValue = version;
            return await SafeDelete.RemoveOrDeactivateAsync(db, audit, product, nameof(Product), id.ToString(), product.Name, () => product.IsActive = false, ct);
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

        inventory.MapGet("/locations", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok(await ProjectLocations(db, db.StockLocations.AsNoTracking().OrderBy(l => l.Id)).ToListAsync(ct)))
            .RequirePermission("inventory.view");

        inventory.MapGet("/locations/{code}", async (string code, AppDbContext db, CancellationToken ct) =>
        {
            var location = await ProjectLocations(db, db.StockLocations.AsNoTracking().Where(l => l.Code == code)).SingleOrDefaultAsync(ct)
                ?? throw AppException.NotFound();
            var stock = await db.StockBatches.AsNoTracking()
                .Where(b => b.Location!.Code == code && b.Quantity > 0)
                .GroupBy(b => new { b.ProductId, b.Product!.Code, b.Product.Name, b.Product.Unit })
                .OrderBy(g => g.Key.Name)
                .Select(g => new LocationStockRow(g.Key.ProductId, g.Key.Code, g.Key.Name, g.Key.Unit, g.Sum(b => b.Quantity), g.Sum(b => b.Quantity * b.UnitCost), g.Min(b => b.ExpiryDate), g.Count()))
                .ToListAsync(ct);
            return Results.Ok(new LocationDetail(location, stock));
        }).RequirePermission("inventory.view");

        inventory.MapGet("/alerts", async (AppDbContext db, CancellationToken ct) =>
        {
            var soon = KabulClock.Today.AddDays(90);
            var expiring = await ProjectBatches(db.StockBatches.AsNoTracking().Where(b => b.Quantity > 0 && b.ExpiryDate != null && b.ExpiryDate <= soon).OrderBy(b => b.ExpiryDate).Take(100)).ToListAsync(ct);
            var low = await ProjectProducts(db, db.Products.AsNoTracking().Where(p => p.IsActive && p.ReorderLevel > 0 && (db.StockBatches.Where(b => b.ProductId == p.Id).Sum(b => (decimal?)b.Quantity) ?? 0) <= p.ReorderLevel).OrderBy(p => p.Name).Take(100)).ToListAsync(ct);
            return Results.Ok(new { lowStock = low, expiring });
        }).RequirePermission("inventory.view");

        inventory.MapGet("/movements", async (AppDbContext db, int? productId, string? location, string? direction, int page = 1, int pageSize = 50, CancellationToken ct = default) =>
        {
            pageSize = Math.Clamp(pageSize, 1, 100);
            page = Math.Max(page, 1);

            var query = db.StockMovements.AsNoTracking();
            if (productId is { } p)
            {
                query = query.Where(m => m.Batch!.ProductId == p);
            }

            if (!string.IsNullOrWhiteSpace(location))
            {
                query = query.Where(m => m.Batch!.Location!.Code == location);
            }

            if (direction == "in")
            {
                query = query.Where(m => m.Quantity > 0);
            }
            else if (direction == "out")
            {
                query = query.Where(m => m.Quantity < 0);
            }

            var total = await query.CountAsync(ct);
            var rows = await query.OrderByDescending(m => m.Id).Skip((page - 1) * pageSize).Take(pageSize)
                .Select(m => new { m.Id, m.Batch!.Product!.Name, Location = m.Batch.Location!.Name, m.Batch.LotNumber, m.Kind, m.Quantity, m.SourceType, m.SourceId, m.Reason, m.CreatedAt, Reversed = db.StockMovements.Any(r => r.SourceType == ReversalSource && r.SourceId == m.Id) })
                .ToListAsync(ct);
            var items = rows.Select(m => new MovementRow(m.Id, m.Name, m.Location, m.LotNumber, m.Kind.ToString(), m.Quantity, m.SourceType, m.SourceId, m.Reason, m.CreatedAt,
                !m.Reversed && m.SourceType != ReversalSource && m.Kind is MovementKind.Adjustment or MovementKind.Consumption or MovementKind.TransferOut)).ToList();
            return Results.Ok(new PagedResult<MovementRow>(items, total, page, pageSize));
        }).RequirePermission("inventory.view");

        // An adjustment or transfer made by mistake is undone with an opposite movement; the history keeps both.
        inventory.MapPost("/movements/{id:long}/reverse", async (long id, ReverseMovementRequest request, AppDbContext db, StockService stock, LedgerService ledger, AuditService audit, Outbox outbox, HttpContext http, CancellationToken ct) =>
        {
            var reason = request.Reason.Trim();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var movement = await db.StockMovements.AsNoTracking().SingleOrDefaultAsync(m => m.Id == id, ct) ?? throw AppException.NotFound();
            if (movement.SourceType == ReversalSource || movement.Kind is not (MovementKind.Adjustment or MovementKind.Consumption or MovementKind.TransferOut))
            {
                throw new AppException(StatusCodes.Status409Conflict, "not_reversible", "این گردش از اینجا برگشت نمی‌شود. خرید و فروش از سند خودشان لغو می‌شوند.");
            }

            var isTransfer = movement.Kind == MovementKind.TransferOut;
            Permission.Require(http, isTransfer ? "inventory.edit" : "inventory.approve");

            // Transfers from one batch take turns on its row lock, so the arriving half is the next one recorded.
            var arrival = !isTransfer ? null : await db.StockMovements.AsNoTracking()
                .Where(m => m.Id > id && m.Kind == MovementKind.TransferIn && m.SourceType == "Transfer" && m.SourceId == movement.SourceId && m.Quantity == -movement.Quantity)
                .OrderBy(m => m.Id).FirstOrDefaultAsync(ct);

            // Locks before the check: two people reversing together must not both see it as not yet reversed.
            var batchIds = new[] { movement.BatchId, arrival?.BatchId }.OfType<long>().Order().ToList();
            var batches = new Dictionary<long, StockBatch>();
            foreach (var batchId in batchIds)
            {
                batches[batchId] = await db.StockBatches.FromSqlInterpolated($"SELECT * FROM StockBatches WHERE Id = {batchId} FOR UPDATE").SingleAsync(ct);
            }

            var earlier = await db.StockMovements.FromSqlInterpolated($"SELECT * FROM StockMovements WHERE SourceType = {ReversalSource} AND SourceId = {id} FOR UPDATE").AsNoTracking().ToListAsync(ct);
            if (earlier.Count > 0)
            {
                throw new AppException(StatusCodes.Status409Conflict, "already_reversed", "این گردش قبلاً برگشت شده است.");
            }

            void Apply(long batchId, MovementKind kind, decimal quantity)
            {
                var batch = batches[batchId];
                if (batch.Quantity + quantity < 0)
                {
                    throw new AppException(StatusCodes.Status409Conflict, "insufficient_stock", "موجودی این بچ برای برگشت کافی نیست، چون بخشی از آن مصرف شده است.");
                }

                batch.Quantity += quantity;
                stock.AddMovement(batch, kind, quantity, ReversalSource, id, reason);
            }

            if (arrival is not null)
            {
                Apply(arrival.BatchId, MovementKind.TransferOut, -arrival.Quantity);
                Apply(movement.BatchId, MovementKind.TransferIn, -movement.Quantity);
            }
            else if (isTransfer)
            {
                throw new AppException(StatusCodes.Status409Conflict, "not_reversible", "گردش مقصد این انتقال پیدا نشد.");
            }
            else
            {
                Apply(movement.BatchId, movement.Kind, -movement.Quantity);
                await ledger.ReverseAsync("Adjustment", "StockMovement", id, ct);
            }

            audit.Add("StockMovementReversed", nameof(StockMovement), id.ToString(), reason);
            outbox.Add("InventoryChanged", new { movementId = id }, Groups.Pharmacy, Groups.Optical, Groups.Inventory);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization();

        inventory.MapGet("/purchases", async (AppDbContext db, int page = 1, int pageSize = 50, CancellationToken ct = default) =>
        {
            pageSize = Math.Clamp(pageSize, 1, 100);
            page = Math.Max(page, 1);
            var total = await db.Purchases.CountAsync(ct);
            var rows = await ProjectPurchases(db.Purchases.AsNoTracking().OrderByDescending(p => p.Id).Skip((page - 1) * pageSize).Take(pageSize)).ToListAsync(ct);
            return Results.Ok(new PagedResult<PurchaseRow>(rows, total, page, pageSize));
        }).RequirePermission("inventory.view");

        inventory.MapGet("/purchases/{id:long}", async (long id, AppDbContext db, CancellationToken ct) =>
        {
            var purchase = await ProjectPurchases(db.Purchases.AsNoTracking().Where(p => p.Id == id)).SingleOrDefaultAsync(ct) ?? throw AppException.NotFound();
            var items = await (
                from i in db.Set<PurchaseItem>().AsNoTracking()
                join b in db.StockBatches on i.BatchId equals b.Id
                where i.PurchaseId == id
                orderby i.Id
                select new PurchaseLineRow(i.ProductId, i.Product!.Name, b.LotNumber, b.ExpiryDate, i.Quantity, i.UnitCost)).ToListAsync(ct);
            return Results.Ok(new PurchaseDetail(purchase, items));
        }).RequirePermission("inventory.view");

        inventory.MapPost("/purchases", async (CreatePurchaseRequest request, AppDbContext db, Idempotency idempotency, NumberService numbers, StockService stock, LedgerService ledger, CurrentUser user, AuditService audit, Outbox outbox, CancellationToken ct) =>
            await idempotency.RunAsync(request, async () =>
            {
                var purchase = await RecordPurchaseAsync(request, null, db, numbers, stock, ledger, user, audit, outbox, ct);
                return (StatusCodes.Status201Created, new { purchase.Id, purchase.PurchaseNumber, purchase.Total });
            }, ct)).RequirePermission("inventory.create");

        inventory.MapPost("/purchases/{id:long}/cancel", async (long id, CancelPurchaseRequest request, AppDbContext db, StockService stock, LedgerService ledger, AuditService audit, Outbox outbox, CancellationToken ct) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await CancelPurchaseAsync(id, request.Version, request.Reason.Trim(), db, stock, ledger, audit, outbox, ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        }).RequirePermission("inventory.cancel");

        // Cancels the mistaken purchase and records the right one together, so stock and the ledger never show one without the other.
        inventory.MapPost("/purchases/{id:long}/correct", async (long id, CorrectPurchaseRequest request, AppDbContext db, NumberService numbers, StockService stock, LedgerService ledger, CurrentUser user, AuditService audit, Outbox outbox, HttpContext http, CancellationToken ct) =>
        {
            Permission.Require(http, "inventory.create");
            var reason = request.Reason.Trim();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var old = await CancelPurchaseAsync(id, request.Version, reason, db, stock, ledger, audit, outbox, ct);
            await db.SaveChangesAsync(ct);

            var replacement = await RecordPurchaseAsync(new CreatePurchaseRequest(null, request.LocationCode, request.Items, request.SupplierId), old.SupplierId, db, numbers, stock, ledger, user, audit, outbox, ct);
            audit.Add("PurchaseCorrected", nameof(Purchase), id.ToString(), $"{replacement.PurchaseNumber}: {reason}");
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { replacement.Id, replacement.PurchaseNumber, replacement.Total });
        }).RequirePermission("inventory.cancel");

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

        pharmacy.MapGet("/sales/{id:long}", async (long id, AppDbContext db, CancellationToken ct) =>
            Results.Ok(await LoadSaleAsync(db, id, SaleKind.Pharmacy, ct))).RequirePermission("pharmacy.view");

        // Returns the mistaken sale and records the right one together. The patient and prescription stay those of the original.
        pharmacy.MapPost("/sales/{id:long}/correct", async (long id, CorrectSaleRequest request, AppDbContext db, SaleService sales, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            Permission.Require(http, "pharmacy.create");
            var reason = request.Reason.Trim();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var old = await db.Sales.AsNoTracking().Where(s => s.Id == id && s.Kind == SaleKind.Pharmacy).Select(s => new { s.PatientId, s.PrescriptionId }).SingleOrDefaultAsync(ct) ?? throw AppException.NotFound();
            await sales.ReturnAsync(id, request.Version, reason, ct);
            var replacement = await sales.CreateAsync(SaleKind.Pharmacy, old.PatientId, old.PrescriptionId, request.Items, [], ct);
            audit.Add("SaleCorrected", nameof(Sale), id.ToString(), $"{replacement.SaleNumber}: {reason}");
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Ok(await ProjectSales(db.Sales.AsNoTracking().Where(s => s.Id == replacement.Id)).SingleAsync(ct));
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
                await sales.ReturnAsync(order.SaleId, saleVersion, Text.Clean(request.Reason) ?? "لغو سفارش عینک", ct);
            }

            order.Status = request.Status;
            order.DeliveredAt = request.Status == OpticalOrderStatus.Delivered ? DateTime.UtcNow : order.DeliveredAt;
            audit.Add("OpticalOrderStatusChanged", nameof(OpticalOrder), id.ToString(), request.Reason is null ? request.Status.ToString() : $"{request.Status}: {request.Reason.Trim()}");
            outbox.Add("OpticalOrderChanged", new { orderId = id }, Groups.Optical, Groups.Cashier);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization();

        optical.MapGet("/orders/{id:long}", async (long id, AppDbContext db, CancellationToken ct) =>
        {
            var order = await db.OpticalOrders.AsNoTracking().SingleOrDefaultAsync(o => o.Id == id, ct) ?? throw AppException.NotFound();
            return Results.Ok(new OpticalOrderDetail(order.Id, order.OrderNumber, order.Description, order.OpticalPrescriptionId, order.Status.ToString(), order.Version, await LoadSaleAsync(db, order.SaleId, SaleKind.Optical, ct)));
        }).RequirePermission("optical.view");

        // Cancels the mistaken order with its sale and records the right one together.
        optical.MapPost("/orders/{id:long}/correct", async (long id, CorrectOpticalOrderRequest request, AppDbContext db, SaleService sales, NumberService numbers, CurrentUser user, AuditService audit, Outbox outbox, HttpContext http, CancellationToken ct) =>
        {
            Permission.Require(http, "optical.create");
            var reason = request.Reason.Trim();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var order = await db.OpticalOrders.SingleOrDefaultAsync(o => o.Id == id, ct) ?? throw AppException.NotFound();
            db.Entry(order).Property(o => o.Version).OriginalValue = request.Version;
            if (!OpticalFlow[order.Status].Contains(OpticalOrderStatus.Cancelled))
            {
                throw new AppException(StatusCodes.Status409Conflict, "invalid_transition", "سفارش تحویل‌شده یا لغوشده اصلاح نمی‌شود.");
            }

            var saleVersion = await db.Sales.Where(s => s.Id == order.SaleId).Select(s => s.Version).SingleAsync(ct);
            await sales.ReturnAsync(order.SaleId, saleVersion, reason, ct);
            order.Status = OpticalOrderStatus.Cancelled;
            await db.SaveChangesAsync(ct);

            var sale = await sales.CreateAsync(SaleKind.Optical, order.PatientId, null, request.Items ?? [], request.CustomLines ?? [], ct);
            var replacement = new OpticalOrder
            {
                OrderNumber = $"OO-{await numbers.NextAsync("optical-order", ct: ct):D6}",
                PatientId = order.PatientId,
                OpticalPrescriptionId = order.OpticalPrescriptionId,
                Description = Text.Clean(request.Description),
                SaleId = sale.Id,
                CreatedByUserId = user.Id,
            };
            db.OpticalOrders.Add(replacement);
            await db.SaveChangesAsync(ct);
            audit.Add("OpticalOrderCreated", nameof(OpticalOrder), replacement.Id.ToString());
            audit.Add("OpticalOrderCorrected", nameof(OpticalOrder), id.ToString(), $"{replacement.OrderNumber}: {reason}");
            outbox.Add("OpticalOrderChanged", new { orderId = replacement.Id }, Groups.Optical, Groups.Cashier);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            var row = await ProjectOrders(db.OpticalOrders.AsNoTracking().Where(o => o.Id == replacement.Id)).SingleAsync(ct);
            return Results.Ok(ToRow(row));
        }).RequirePermission("optical.cancel");
    }

    private const string ReversalSource = "Reversal";

    /// <param name="keptSupplierId">The supplier of the purchase being corrected, which may since have been deactivated.</param>
    private static async Task<Purchase> RecordPurchaseAsync(CreatePurchaseRequest request, int? keptSupplierId, AppDbContext db, NumberService numbers, StockService stock, LedgerService ledger, CurrentUser user, AuditService audit, Outbox outbox, CancellationToken ct)
    {
        var locationId = await db.StockLocations.Where(l => l.Code == request.LocationCode).Select(l => (int?)l.Id).SingleOrDefaultAsync(ct)
            ?? throw new AppException(StatusCodes.Status400BadRequest, "unknown_location", "محل نگهداری معتبر نیست.");

        var supplierName = Text.Clean(request.SupplierName);
        if (request.SupplierId is { } supplierId)
        {
            supplierName = await db.Suppliers.Where(s => s.Id == supplierId && (s.IsActive || s.Id == keptSupplierId)).Select(s => s.Name).SingleOrDefaultAsync(ct)
                ?? throw new AppException(400, "unknown_supplier", "تأمین‌کننده انتخاب‌شده معتبر یا فعال نیست.");
        }

        var purchase = new Purchase
        {
            PurchaseNumber = $"PU-{await numbers.NextAsync("purchase", ct: ct):D6}",
            SupplierName = supplierName,
            SupplierId = request.SupplierId,
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
        return purchase;
    }

    /// <summary>Takes the purchased quantities back out of stock and reverses the ledger entry. Stock already sold cannot be taken back.</summary>
    private static async Task<Purchase> CancelPurchaseAsync(long id, int version, string reason, AppDbContext db, StockService stock, LedgerService ledger, AuditService audit, Outbox outbox, CancellationToken ct)
    {
        var purchase = await db.Purchases.FromSqlInterpolated($"SELECT * FROM Purchases WHERE Id = {id} FOR UPDATE").SingleOrDefaultAsync(ct) ?? throw AppException.NotFound();
        db.Entry(purchase).Property(p => p.Version).OriginalValue = version;
        if (purchase.IsCancelled)
        {
            throw new AppException(StatusCodes.Status409Conflict, "already_cancelled", "این خرید قبلاً لغو شده است.");
        }

        var items = await db.Set<PurchaseItem>().AsNoTracking().Where(i => i.PurchaseId == id).OrderBy(i => i.BatchId).ToListAsync(ct);
        foreach (var item in items)
        {
            var batch = await db.StockBatches.FromSqlInterpolated($"SELECT * FROM StockBatches WHERE Id = {item.BatchId} FOR UPDATE").SingleAsync(ct);
            if (batch.Quantity < item.Quantity)
            {
                throw new AppException(StatusCodes.Status409Conflict, "purchase_stock_used", "بخشی از این خرید فروخته یا منتقل شده است و خرید لغو نمی‌شود. ابتدا موجودی را برگردانید یا تعدیل ثبت کنید.");
            }

            batch.Quantity -= item.Quantity;
            stock.AddMovement(batch, MovementKind.PurchaseCancel, -item.Quantity, nameof(Purchase), id, reason);
        }

        await ledger.ReverseAsync("Purchase", nameof(Purchase), id, ct);
        purchase.IsCancelled = true;
        purchase.CancelReason = reason;
        audit.Add("PurchaseCancelled", nameof(Purchase), id.ToString(), reason);
        outbox.Add("InventoryChanged", new { purchaseId = id }, Groups.Pharmacy, Groups.Optical, Groups.Inventory);
        return purchase;
    }

    private static async Task<SaleDetail> LoadSaleAsync(AppDbContext db, long id, SaleKind kind, CancellationToken ct)
    {
        var sale = await db.Sales.AsNoTracking().Where(s => s.Id == id && s.Kind == kind)
            .Select(s => new { s.Id, s.SaleNumber, s.PatientId, PatientName = s.Patient != null ? (s.Patient.FirstName + " " + (s.Patient.LastName ?? "")).Trim() : null, s.PrescriptionId, s.IsReturned, s.Version, s.InvoiceId })
            .SingleOrDefaultAsync(ct) ?? throw AppException.NotFound();

        // One line may have been taken from several batches; the form wants it back as one.
        var parts = await db.Set<SaleItem>().AsNoTracking().Where(i => i.SaleId == id)
            .Select(i => new { i.ProductId, i.PrescriptionItemId, i.Quantity, i.Product!.Name, Kind = i.Product.Kind, i.Product.Unit, i.Product.SalePrice, i.Product.IsActive, OnHand = db.StockBatches.Where(b => b.ProductId == i.ProductId).Sum(b => (decimal?)b.Quantity) ?? 0 })
            .ToListAsync(ct);
        var items = parts.GroupBy(p => new { p.ProductId, p.PrescriptionItemId })
            .Select(g => new SaleLineRow(g.Key.ProductId, g.First().Name, g.First().Kind.ToString(), g.First().Unit, g.First().SalePrice, g.First().OnHand, g.First().IsActive, g.Sum(p => p.Quantity), g.Key.PrescriptionItemId))
            .ToList();
        var custom = await db.InvoiceItems.AsNoTracking().Where(i => i.InvoiceId == sale.InvoiceId && !i.IsVoid && i.SourceType == null)
            .OrderBy(i => i.Id).Select(i => new CustomLineRow(i.Description, i.LineTotal)).ToListAsync(ct);
        return new SaleDetail(sale.Id, sale.SaleNumber, sale.PatientId, sale.PatientName, sale.PrescriptionId, sale.IsReturned, sale.Version, items, custom);
    }

    private static IQueryable<PurchaseRow> ProjectPurchases(IQueryable<Purchase> purchases) => purchases.Select(p => new PurchaseRow(
        p.Id, p.PurchaseNumber, p.SupplierId, p.SupplierName, p.Location!.Code, p.Location.Name, p.Total, p.IsCancelled, p.CancelReason, p.CreatedAt, p.Version));

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

    private static IQueryable<LocationSummary> ProjectLocations(AppDbContext db, IQueryable<StockLocation> locations) => locations.Select(l => new LocationSummary(
        l.Code,
        l.Name,
        db.StockBatches.Where(b => b.LocationId == l.Id && b.Quantity > 0).Select(b => b.ProductId).Distinct().Count(),
        db.StockBatches.Count(b => b.LocationId == l.Id && b.Quantity > 0),
        db.StockBatches.Where(b => b.LocationId == l.Id && b.Quantity > 0).Sum(b => (decimal?)(b.Quantity * b.UnitCost)) ?? 0,
        db.StockMovements.Where(m => m.Batch!.LocationId == l.Id).Max(m => (DateTime?)m.CreatedAt)));

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
