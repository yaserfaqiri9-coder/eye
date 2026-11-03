using System.ComponentModel.DataAnnotations;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AlShifa.Api.Features.Users;

public sealed record StaffRow(long Id, string FullName, string? Phone, bool IsDoctor, bool IsActive, int? DepartmentId, string? DepartmentName, long? UserId, string? UserName, int Version, string? Specialty, string? Biography, string? Website, string? Facebook, string? Twitter, string? Google, int Avatar);

public sealed record SaveStaffRequest(
    [Required, MaxLength(150)] string FullName,
    [MaxLength(30)] string? Phone,
    bool IsDoctor,
    bool IsActive,
    int? DepartmentId,
    long? UserId,
    int? Version = null,
    [MaxLength(150)] string? Specialty = null,
    [MaxLength(1000)] string? Biography = null,
    [MaxLength(500)] string? Website = null,
    [MaxLength(500)] string? Facebook = null,
    [MaxLength(500)] string? Twitter = null,
    [MaxLength(500)] string? Google = null,
    [Range(12,18)] int Avatar = 12);

public static class StaffEndpoints
{
    public static void MapStaff(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/staff").WithTags("Staff");

        group.MapGet("/", async (AppDbContext db, string? q, bool? isDoctor = null, int page = 1, int pageSize = 25, CancellationToken ct = default) =>
        {
            pageSize = Math.Clamp(pageSize, 1, 100);
            page = Math.Max(page, 1);

            var query = db.Staff.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(q))
            {
                query = query.Where(s => s.FullName.StartsWith(q));
            }

            if (isDoctor is { } doctor) query = query.Where(s => s.IsDoctor == doctor);
            var total = await query.CountAsync(ct);
            var items = await query
                .OrderBy(s => s.FullName)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(s => new StaffRow(s.Id, s.FullName, s.Phone, s.IsDoctor, s.IsActive, s.DepartmentId, s.Department != null ? s.Department.Name : null, s.UserId, s.User != null ? s.User.UserName : null, s.Version, s.Specialty, s.Biography, s.Website, s.Facebook, s.Twitter, s.Google, s.Avatar))
                .ToListAsync(ct);

            return Results.Ok(new PagedResult<StaffRow>(items, total, page, pageSize));
        }).RequirePermission("users.view");

        group.MapPost("/", async (SaveStaffRequest request, AppDbContext db, AuditService audit, CancellationToken ct) =>
        {
            await EnsureUserExistsAsync(db, request, ct);
            var staff = new Staff();
            Apply(staff, request);
            db.Staff.Add(staff);
            await db.SaveChangesAsync(ct);
            audit.Add("StaffCreated", "Staff", staff.Id.ToString());
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/staff/{staff.Id}", new { staff.Id });
        }).RequirePermission("users.create");

        group.MapPut("/{id:long}", async (long id, SaveStaffRequest request, AppDbContext db, AuditService audit, CancellationToken ct) =>
        {
            if (request.Version is null)
            {
                throw new AppException(StatusCodes.Status400BadRequest, "version_required", "نسخه رکورد ارسال نشده است.");
            }

            await EnsureUserExistsAsync(db, request, ct);
            var staff = await db.Staff.SingleOrDefaultAsync(s => s.Id == id, ct) ?? throw AppException.NotFound();
            db.Entry(staff).Property(s => s.Version).OriginalValue = request.Version.Value;
            Apply(staff, request);
            audit.Add("StaffEdited", "Staff", id.ToString());
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequirePermission("users.manage");
    }

    /// <summary>The link decides whose queue a doctor's patients appear in, so a wrong one must fail clearly.</summary>
    private static async Task EnsureUserExistsAsync(AppDbContext db, SaveStaffRequest r, CancellationToken ct)
    {
        if (r.DepartmentId is { } departmentId && !await db.Departments.AnyAsync(d => d.Id == departmentId && d.IsActive, ct))
            throw new AppException(400, "unknown_department", "بخش انتخاب‌شده فعال نیست.");
        foreach (var link in new[] { r.Website, r.Facebook, r.Twitter, r.Google })
            if (!string.IsNullOrWhiteSpace(link) && (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http")))
                throw new AppException(400, "invalid_url", "پیوند باید با https:// یا http:// آغاز شود.");
        if (r.UserId is { } userId && !await db.Users.AnyAsync(u => u.Id == userId, ct))
        {
            throw new AppException(StatusCodes.Status400BadRequest, "unknown_user", "کاربر انتخاب‌شده پیدا نشد.");
        }
    }

    private static void Apply(Staff staff, SaveStaffRequest r)
    {
        staff.Specialty = Text.Clean(r.Specialty);
        staff.Biography = Text.Clean(r.Biography);
        staff.Website = Text.Clean(r.Website);
        staff.Facebook = Text.Clean(r.Facebook);
        staff.Twitter = Text.Clean(r.Twitter);
        staff.Google = Text.Clean(r.Google);
        staff.Avatar = r.Avatar;
        staff.FullName = r.FullName.Trim();
        staff.Phone = string.IsNullOrWhiteSpace(r.Phone) ? null : r.Phone.Trim();
        staff.IsDoctor = r.IsDoctor;
        staff.IsActive = r.IsActive;
        staff.DepartmentId = r.DepartmentId;
        staff.UserId = r.UserId;
    }
}
