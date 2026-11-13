using System.ComponentModel.DataAnnotations;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Inventory;
using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AlShifa.Api.Features.Users;

/// <summary>The code is not sent: the system gives a new department the next short code, and it never changes.</summary>
public sealed record SaveDepartmentRequest([Required, MaxLength(100)] string Name, bool IsActive = true, int? Version = null);
public sealed record SaveSupplierRequest([Required, MaxLength(150)] string Name, [MaxLength(30)] string? Phone, [MaxLength(300)] string? Address, bool IsActive = true, int? Version = null);

public static class DirectoryEndpoints
{
    public static void MapDirectories(this IEndpointRouteBuilder app)
    {
        var departments = app.MapGroup("/api/departments").WithTags("Departments");
        departments.MapGet("/", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Departments.AsNoTracking().OrderBy(d => d.Id).ToListAsync(ct))).RequirePermission("settings.view");
        departments.MapPost("/", async (SaveDepartmentRequest r, AppDbContext db, NumberService numbers, AuditService audit, CancellationToken ct) =>
        {
            ValidateDepartment(r);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var code = await ShortCodes.NextAsync(numbers, "department", c => db.Departments.AnyAsync(d => d.Code == c, ct), ct);
            var row = new Department { Code = code, Name = r.Name.Trim(), IsActive = r.IsActive };
            db.Departments.Add(row);
            audit.Add("DepartmentCreated", "Department", null, row.Name);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Created($"/api/departments/{row.Id}", row);
        }).RequirePermission("settings.edit");
        departments.MapPut("/{id:int}", async (int id, SaveDepartmentRequest r, AppDbContext db, AuditService audit, CancellationToken ct) =>
        {
            RequireVersion(r.Version);
            ValidateDepartment(r);
            var row = await db.Departments.SingleOrDefaultAsync(d => d.Id == id, ct) ?? throw AppException.NotFound();
            db.Entry(row).Property(d => d.Version).OriginalValue = r.Version!.Value;
            row.Name = r.Name.Trim(); row.IsActive = r.IsActive;
            audit.Add("DepartmentEdited", "Department", id.ToString());
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequirePermission("settings.edit");
        departments.MapDelete("/{id:int}", async (int id, int version, AppDbContext db, AuditService audit, CancellationToken ct) =>
        {
            var row = await db.Departments.SingleOrDefaultAsync(d => d.Id == id, ct) ?? throw AppException.NotFound();
            db.Entry(row).Property(d => d.Version).OriginalValue = version;
            if (Seeder.IsSystemDepartment(row.Code)) throw new AppException(409, "system_department", "این بخش سیستمی است و حذف نمی‌شود. در صورت نیاز آن را از ویرایش غیرفعال کنید.");
            return await SafeDelete.RemoveOrDeactivateAsync(db, audit, row, "Department", id.ToString(), row.Name, () => row.IsActive = false, ct);
        }).RequirePermission("settings.edit");

        var suppliers = app.MapGroup("/api/suppliers").WithTags("Suppliers");
        suppliers.MapGet("/", async (AppDbContext db, bool activeOnly = false, CancellationToken ct = default) =>
            Results.Ok(await db.Suppliers.AsNoTracking().Where(s => !activeOnly || s.IsActive).OrderBy(s => s.Name).ToListAsync(ct))).RequirePermission("inventory.view");
        suppliers.MapPost("/", async (SaveSupplierRequest r, AppDbContext db, AuditService audit, CancellationToken ct) =>
        {
            await ValidateSupplier(r, db, null, ct);
            var row = new Supplier { Name = r.Name.Trim(), Phone = Text.Clean(r.Phone), Address = Text.Clean(r.Address), IsActive = r.IsActive };
            db.Suppliers.Add(row);
            audit.Add("SupplierCreated", "Supplier", null, row.Name);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/suppliers/{row.Id}", row);
        }).RequirePermission("inventory.create");
        suppliers.MapPut("/{id:int}", async (int id, SaveSupplierRequest r, AppDbContext db, AuditService audit, CancellationToken ct) =>
        {
            RequireVersion(r.Version);
            await ValidateSupplier(r, db, id, ct);
            var row = await db.Suppliers.SingleOrDefaultAsync(s => s.Id == id, ct) ?? throw AppException.NotFound();
            db.Entry(row).Property(s => s.Version).OriginalValue = r.Version!.Value;
            row.Name = r.Name.Trim(); row.Phone = Text.Clean(r.Phone); row.Address = Text.Clean(r.Address); row.IsActive = r.IsActive;
            audit.Add("SupplierEdited", "Supplier", id.ToString());
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequirePermission("inventory.create");
        suppliers.MapDelete("/{id:int}", async (int id, int version, AppDbContext db, AuditService audit, CancellationToken ct) =>
        {
            var row = await db.Suppliers.SingleOrDefaultAsync(s => s.Id == id, ct) ?? throw AppException.NotFound();
            db.Entry(row).Property(s => s.Version).OriginalValue = version;
            return await SafeDelete.RemoveOrDeactivateAsync(db, audit, row, "Supplier", id.ToString(), row.Name, () => row.IsActive = false, ct);
        }).RequirePermission("inventory.create");
    }

    private static void RequireVersion(int? version)
    {
        if (version is null) throw new AppException(400, "version_required", "نسخه رکورد ارسال نشده است.");
    }
    private static void ValidateDepartment(SaveDepartmentRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Name)) throw new AppException(400, "required", "نام بخش ضروری است.");
    }
    private static async Task ValidateSupplier(SaveSupplierRequest r, AppDbContext db, int? id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(r.Name)) throw new AppException(400, "required", "نام تأمین‌کننده ضروری است.");
        if (await db.Suppliers.AnyAsync(s => s.Id != id && s.Name == r.Name.Trim(), ct)) throw new AppException(409, "duplicate_supplier", "این تأمین‌کننده قبلاً ثبت شده است.");
    }
}
