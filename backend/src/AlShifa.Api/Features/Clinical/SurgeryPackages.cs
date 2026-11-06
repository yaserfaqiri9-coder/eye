using System.ComponentModel.DataAnnotations;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Finance;
using AlShifa.Api.Features.Inventory;
using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AlShifa.Api.Features.Clinical;

/// <summary>
/// What one kind of operation uses, such as a cataract kit. The patient pays only the operation's fee; the drugs and
/// supplies are inside it, so they leave stock at cost and are never invoiced on their own.
/// </summary>
public class SurgeryPackage
{
    public int Id { get; set; }

    [MaxLength(200)]
    public string Name { get; set; } = "";

    /// <summary>The fee the patient pays for the whole package.</summary>
    public int ServiceId { get; set; }

    public Service? Service { get; set; }

    /// <summary>The store the supplies are usually taken from; the theatre can pick another when it records them.</summary>
    public int LocationId { get; set; }

    public StockLocation? Location { get; set; }

    public bool IsActive { get; set; } = true;

    public int Version { get; set; }

    public List<SurgeryPackageItem> Items { get; set; } = [];
}

public class SurgeryPackageItem
{
    public long Id { get; set; }

    public int PackageId { get; set; }

    public int ProductId { get; set; }

    public Product? Product { get; set; }

    public decimal Quantity { get; set; }
}

public sealed record PackageItemInput(int ProductId, [Range(0.001, 100000)] decimal Quantity);

public sealed record SavePackageRequest(
    [Required, MaxLength(200)] string Name,
    int ServiceId,
    [Required, MaxLength(30)] string LocationCode,
    [Required, MinLength(1)] PackageItemInput[] Items,
    bool IsActive = true,
    int? Version = null);

public sealed record PackageItemRow(int ProductId, string ProductName, string Unit, decimal Quantity);

public sealed record PackageRow(int Id, string Name, int ServiceId, string ServiceName, decimal? Price, string LocationCode, string LocationName, bool IsActive, int Version, IReadOnlyList<PackageItemRow> Items);

public sealed record SupplyLocationRow(string Code, string Name);

public sealed record SupplyStockRow(int ProductId, string Code, string Name, string Unit, decimal OnHand);

public sealed record RecordSuppliesRequest([Required, MaxLength(30)] string LocationCode, PackageItemInput[] Items, int Version);

public sealed record SupplyUsedRow(int ProductId, string ProductName, string Unit, decimal Quantity, decimal Cost);

public sealed record SuppliesRow(bool Recorded, DateTime? RecordedAt, string? LocationName, int? PackageId, IReadOnlyList<SupplyUsedRow> Items);

public static class SurgeryPackageEndpoints
{
    public const string Source = nameof(SurgeryCase);

    /// <summary>Supplies are used in the theatre, so they are recorded once the operation has begun.</summary>
    private static readonly SurgeryStatus[] Started = [SurgeryStatus.InProgress, SurgeryStatus.Completed, SurgeryStatus.FollowUp];

    public static void MapSurgeryPackages(this IEndpointRouteBuilder app)
    {
        var packages = app.MapGroup("/api/surgery-packages").WithTags("Surgery");

        packages.MapGet("/", async (AppDbContext db, bool all = false, CancellationToken ct = default) =>
        {
            var query = db.SurgeryPackages.AsNoTracking();
            if (!all)
            {
                query = query.Where(p => p.IsActive);
            }

            return Results.Ok(await ProjectAsync(db, query, ct));
        }).RequirePermission("surgery.view");

        packages.MapPost("/", async (SavePackageRequest request, AppDbContext db, AuditService audit, CancellationToken ct) =>
        {
            var package = new SurgeryPackage();
            await ApplyAsync(db, package, request, ct);
            db.SurgeryPackages.Add(package);
            await db.SaveChangesAsync(ct);
            audit.Add("SurgeryPackageCreated", nameof(SurgeryPackage), package.Id.ToString(), package.Name);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/surgery-packages/{package.Id}", (await ProjectAsync(db, db.SurgeryPackages.Where(p => p.Id == package.Id), ct)).Single());
        }).RequirePermission("surgery.approve");

        // A change applies to operations recorded after it. Supplies already taken stay as they were.
        packages.MapPut("/{id:int}", async (int id, SavePackageRequest request, AppDbContext db, AuditService audit, CancellationToken ct) =>
        {
            var package = await db.SurgeryPackages.Include(p => p.Items).SingleOrDefaultAsync(p => p.Id == id, ct) ?? throw AppException.NotFound();
            db.Entry(package).Property(p => p.Version).OriginalValue = request.Version ?? -1;
            db.SurgeryPackageItems.RemoveRange(package.Items);
            package.Items.Clear();
            await ApplyAsync(db, package, request, ct);
            audit.Add("SurgeryPackageEdited", nameof(SurgeryPackage), id.ToString(), package.Name);
            await db.SaveChangesAsync(ct);
            return Results.Ok((await ProjectAsync(db, db.SurgeryPackages.Where(p => p.Id == id), ct)).Single());
        }).RequirePermission("surgery.approve");

        packages.MapGet("/locations", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok(await db.StockLocations.AsNoTracking().OrderBy(l => l.Id).Select(l => new SupplyLocationRow(l.Code, l.Name)).ToListAsync(ct)))
            .RequirePermission("surgery.view");

        // The theatre sees only what it may take: active products, with the usable (unexpired) stock of one store.
        packages.MapGet("/stock", async (AppDbContext db, string location, string? q, string? ids, CancellationToken ct) =>
        {
            var locationId = await db.StockLocations.Where(l => l.Code == location).Select(l => (int?)l.Id).SingleOrDefaultAsync(ct)
                ?? throw new AppException(StatusCodes.Status400BadRequest, "unknown_location", "محل نگهداری معتبر نیست.");
            var today = KabulClock.Today;
            var query = db.Products.AsNoTracking().Where(p => p.IsActive);
            var wanted = (ids ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => int.TryParse(s, out var n) ? n : 0).Where(n => n > 0).ToList();
            if (wanted.Count > 0)
            {
                query = db.Products.AsNoTracking().Where(p => wanted.Contains(p.Id));
            }
            else if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim();
                query = query.Where(p => p.Name.Contains(term) || p.Code.Contains(term));
            }

            var rows = await query.OrderBy(p => p.Name).Take(wanted.Count > 0 ? 200 : 20).Select(p => new SupplyStockRow(
                p.Id, p.Code, p.Name, p.Unit,
                db.StockBatches.Where(b => b.ProductId == p.Id && b.LocationId == locationId && b.Quantity > 0 && (b.ExpiryDate == null || b.ExpiryDate >= today)).Sum(b => (decimal?)b.Quantity) ?? 0))
                .ToListAsync(ct);
            return Results.Ok(rows);
        }).RequirePermission("surgery.view");

        var surgeries = app.MapGroup("/api/surgeries").WithTags("Surgery");

        surgeries.MapGet("/{id:long}/supplies", async (long id, AppDbContext db, CancellationToken ct) =>
        {
            var surgery = await db.SurgeryCases.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw AppException.NotFound();
            return Results.Ok(await SuppliesAsync(db, surgery, ct));
        }).RequirePermission("surgery.edit");

        surgeries.MapPost("/{id:long}/supplies", async (long id, RecordSuppliesRequest request, AppDbContext db, ClinicalAccess access, StockService stock, LedgerService ledger, CurrentUser user, AuditService audit, Outbox outbox, CancellationToken ct) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            // The row lock makes a second recording wait, and then it sees the first one and stops.
            var surgery = await db.SurgeryCases.FromSqlInterpolated($"SELECT * FROM SurgeryCases WHERE Id = {id} FOR UPDATE").SingleOrDefaultAsync(ct) ?? throw AppException.NotFound();
            db.Entry(surgery).Property(c => c.Version).OriginalValue = request.Version;
            await access.EnsureResponsibleAsync(surgery.VisitId, ct, surgery.ResponsibleUserId);

            if (!Started.Contains(surgery.Status))
            {
                throw new AppException(StatusCodes.Status409Conflict, "case_not_started", "مواد مصرفی پس از شروع عملیات ثبت می‌شود.");
            }

            if (surgery.SuppliesRecordedAt is not null)
            {
                throw new AppException(StatusCodes.Status409Conflict, "supplies_recorded", "مواد مصرفی این عملیات قبلاً ثبت شده است. اشتباه را از بخش گدام برگشت دهید.");
            }

            var location = await db.StockLocations.SingleOrDefaultAsync(l => l.Code == request.LocationCode, ct)
                ?? throw new AppException(StatusCodes.Status400BadRequest, "unknown_location", "محل نگهداری معتبر نیست.");

            // One line per product, taken in product order so two theatres never lock the same batches in opposite order.
            var lines = (request.Items ?? []).GroupBy(i => i.ProductId).Select(g => (ProductId: g.Key, Quantity: g.Sum(i => i.Quantity))).OrderBy(l => l.ProductId).ToList();
            if (lines.Any(l => l.Quantity <= 0))
            {
                throw new AppException(StatusCodes.Status400BadRequest, "invalid_quantity", "مقدار هر جنس باید بیشتر از صفر باشد.");
            }

            var productIds = lines.Select(l => l.ProductId).ToList();
            var names = await db.Products.Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
            if (names.Count != lines.Count)
            {
                throw new AppException(StatusCodes.Status400BadRequest, "unknown_product", "یکی از اجناس معتبر نیست.");
            }

            var reason = $"مصرف عملیات {surgery.CaseNumber}";
            foreach (var line in lines)
            {
                try
                {
                    await stock.TakeAsync(line.ProductId, location.Id, line.Quantity, Source, id, MovementKind.Consumption, reason, ct);
                }
                catch (AppException e) when (e.Code == "insufficient_stock")
                {
                    throw new AppException(StatusCodes.Status409Conflict, "insufficient_stock", $"موجودی «{names[line.ProductId]}» در {location.Name} کافی نیست. مقدار را کم کنید، گدام دیگری انتخاب کنید یا جنس را به این گدام انتقال دهید.");
                }
            }

            var taken = db.ChangeTracker.Entries<StockMovement>().Where(e => e.State == EntityState.Added).Select(e => e.Entity).ToList();
            surgery.SuppliesRecordedAt = DateTime.UtcNow;
            surgery.SuppliesRecordedByUserId = user.Id;
            surgery.SuppliesLocationId = location.Id;
            await db.SaveChangesAsync(ct);

            // Each movement carries its own cost entry, so one wrong line can be reversed alone from the stock page.
            foreach (var movement in taken)
            {
                var value = decimal.Round(Math.Abs(movement.Quantity) * movement.Batch!.UnitCost, 2, MidpointRounding.AwayFromZero);
                ledger.Post("Adjustment", "StockMovement", movement.Id, reason, (Accounts.CostOfGoods, value, 0), (Accounts.Inventory, 0, value));
            }

            audit.Add("SurgerySuppliesRecorded", nameof(SurgeryCase), id.ToString(), location.Code);
            outbox.Add("InventoryChanged", new { surgeryId = id }, Groups.Pharmacy, Groups.Optical, Groups.Inventory);
            outbox.Add("SurgeryScheduled", new { surgeryId = id }, Groups.Surgery, Groups.Doctor);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return Results.Ok(await SuppliesAsync(db, surgery, ct));
        }).RequirePermission("surgery.edit");
    }

    /// <summary>What the case used, net of anything later reversed from the stock page.</summary>
    private static async Task<SuppliesRow> SuppliesAsync(AppDbContext db, SurgeryCase surgery, CancellationToken ct)
    {
        var used = await db.StockMovements.AsNoTracking()
            .Where(m => m.SourceType == Source && m.SourceId == surgery.Id && m.Kind == MovementKind.Consumption)
            .Where(m => !db.StockMovements.Any(r => r.SourceType == "Reversal" && r.SourceId == m.Id))
            .Select(m => new { m.Batch!.ProductId, m.Batch.Product!.Name, m.Batch.Product.Unit, m.Quantity, m.Batch.UnitCost })
            .ToListAsync(ct);
        var items = used.GroupBy(m => m.ProductId)
            .Select(g => new SupplyUsedRow(g.Key, g.First().Name, g.First().Unit, -g.Sum(m => m.Quantity), decimal.Round(g.Sum(m => -m.Quantity * m.UnitCost), 2, MidpointRounding.AwayFromZero)))
            .OrderBy(r => r.ProductName)
            .ToList();
        var locationName = surgery.SuppliesLocationId is { } locationId
            ? await db.StockLocations.Where(l => l.Id == locationId).Select(l => l.Name).SingleAsync(ct)
            : null;
        return new SuppliesRow(surgery.SuppliesRecordedAt is not null, surgery.SuppliesRecordedAt, locationName, surgery.PackageId, items);
    }

    private static async Task ApplyAsync(AppDbContext db, SurgeryPackage package, SavePackageRequest request, CancellationToken ct)
    {
        var name = request.Name.Trim();
        if (await db.SurgeryPackages.AnyAsync(p => p.Name == name && p.Id != package.Id, ct))
        {
            throw new AppException(StatusCodes.Status409Conflict, "duplicate_name", "پکیجی با همین نام وجود دارد.");
        }

        if (!await db.Services.AnyAsync(s => s.Id == request.ServiceId && s.IsActive, ct))
        {
            throw new AppException(StatusCodes.Status400BadRequest, "unknown_service", "خدمت انتخاب‌شده معتبر نیست.");
        }

        var locationId = await db.StockLocations.Where(l => l.Code == request.LocationCode).Select(l => (int?)l.Id).SingleOrDefaultAsync(ct)
            ?? throw new AppException(StatusCodes.Status400BadRequest, "unknown_location", "محل نگهداری معتبر نیست.");

        var lines = request.Items.GroupBy(i => i.ProductId).Select(g => new SurgeryPackageItem { ProductId = g.Key, Quantity = g.Sum(i => i.Quantity) }).ToList();
        var productIds = lines.Select(l => l.ProductId).ToList();
        if (await db.Products.CountAsync(p => productIds.Contains(p.Id) && p.IsActive, ct) != lines.Count)
        {
            throw new AppException(StatusCodes.Status400BadRequest, "unknown_product", "یکی از اجناس معتبر یا فعال نیست.");
        }

        package.Name = name;
        package.ServiceId = request.ServiceId;
        package.LocationId = locationId;
        package.IsActive = request.IsActive;
        package.Items.AddRange(lines);
    }

    private static async Task<List<PackageRow>> ProjectAsync(AppDbContext db, IQueryable<SurgeryPackage> query, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var rows = await query.OrderBy(p => p.Name).Select(p => new
        {
            p.Id,
            p.Name,
            p.ServiceId,
            ServiceName = p.Service!.Name,
            Price = db.ServicePrices.Where(s => s.ServiceId == p.ServiceId && s.EffectiveFrom <= now).OrderByDescending(s => s.EffectiveFrom).ThenByDescending(s => s.Id).Select(s => (decimal?)s.Price).FirstOrDefault(),
            LocationCode = p.Location!.Code,
            LocationName = p.Location.Name,
            p.IsActive,
            p.Version,
            Items = p.Items.OrderBy(i => i.Product!.Name).Select(i => new PackageItemRow(i.ProductId, i.Product!.Name, i.Product.Unit, i.Quantity)).ToList(),
        }).ToListAsync(ct);
        return rows.Select(r => new PackageRow(r.Id, r.Name, r.ServiceId, r.ServiceName, r.Price, r.LocationCode, r.LocationName, r.IsActive, r.Version, r.Items)).ToList();
    }
}
