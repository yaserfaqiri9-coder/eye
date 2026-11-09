using System.ComponentModel.DataAnnotations;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Realtime;
using AlShifa.Api.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AlShifa.Api.Features.Users;

public sealed record UserListItem(long Id, string UserName, string FullName, bool IsActive, IReadOnlyList<string> Roles);

public sealed record CreateUserRequest(
    [Required, MaxLength(100)] string UserName,
    [Required, MaxLength(150)] string FullName,
    [Required, MaxLength(200)] string Password,
    [Required, MinLength(1)] string[] Roles);

public sealed record SetRolesRequest([Required, MinLength(1)] string[] Roles);

public sealed record EditUserRequest([Required, MaxLength(150)] string FullName);

public sealed record ResetPasswordRequest([Required, MaxLength(200)] string NewPassword);

public static class UserEndpoints
{
    public static void MapUsers(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/users").WithTags("Users");

        group.MapGet("/", async (AppDbContext db, int page = 1, int pageSize = 25, string? q = null, CancellationToken ct = default) =>
        {
            pageSize = Math.Clamp(pageSize, 1, 100);
            page = Math.Max(page, 1);

            var query = db.Users.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(q))
            {
                query = query.Where(u => u.UserName!.StartsWith(q) || u.FullName.StartsWith(q));
            }

            var total = await query.CountAsync(ct);
            var users = await query
                .OrderBy(u => u.UserName)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(u => new { u.Id, u.UserName, u.FullName, u.IsActive })
                .ToListAsync(ct);

            var ids = users.Select(u => u.Id).ToList();
            var roles = await (
                from ur in db.UserRoles
                join r in db.Roles on ur.RoleId equals r.Id
                where ids.Contains(ur.UserId)
                select new { ur.UserId, r.Name }).ToListAsync(ct);

            var items = users.Select(u => new UserListItem(
                u.Id,
                u.UserName!,
                u.FullName,
                u.IsActive,
                roles.Where(r => r.UserId == u.Id).Select(r => r.Name!).ToList())).ToList();

            return Results.Ok(new PagedResult<UserListItem>(items, total, page, pageSize));
        }).RequirePermission("users.view");

        group.MapPost("/", async (CreateUserRequest request, UserManager<AppUser> users, AuditService audit, AppDbContext db) =>
        {
            EnsureKnownRoles(request.Roles);

            var user = new AppUser { UserName = request.UserName.Trim(), FullName = request.FullName.Trim() };
            var created = await users.CreateAsync(user, request.Password);
            if (!created.Succeeded)
            {
                throw ToAppException(created);
            }

            await users.AddToRolesAsync(user, request.Roles);
            audit.Add("UserCreated", "User", user.Id.ToString(), string.Join(",", request.Roles));
            await db.SaveChangesAsync();
            return Results.Created($"/api/users/{user.Id}", new { user.Id });
        }).RequirePermission("users.create");

        group.MapPut("/{id:long}", async (long id, EditUserRequest request, UserManager<AppUser> users, AuditService audit, AppDbContext db) =>
        {
            var user = await users.FindByIdAsync(id.ToString()) ?? throw AppException.NotFound();
            var previous = user.FullName;
            user.FullName = request.FullName.Trim();
            var updated = await users.UpdateAsync(user);
            if (!updated.Succeeded)
            {
                throw ToAppException(updated);
            }

            audit.Add("UserEdited", "User", id.ToString(), previous);
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequirePermission("users.manage");

        group.MapPut("/{id:long}/roles", async (long id, SetRolesRequest request, UserManager<AppUser> users, AuditService audit, AppDbContext db, ConnectionTracker connections) =>
        {
            EnsureKnownRoles(request.Roles);
            var user = await users.FindByIdAsync(id.ToString()) ?? throw AppException.NotFound();

            var current = await users.GetRolesAsync(user);
            await users.RemoveFromRolesAsync(user, current.Except(request.Roles));
            await users.AddToRolesAsync(user, request.Roles.Except(current));
            await users.UpdateSecurityStampAsync(user);

            audit.Add("UserRolesChanged", "User", user.Id.ToString(), $"{string.Join(",", current)} -> {string.Join(",", request.Roles)}");
            await db.SaveChangesAsync();
            connections.Disconnect(id);
            return Results.NoContent();
        }).RequirePermission("users.manage");

        group.MapPost("/{id:long}/deactivate", async (long id, UserManager<AppUser> users, CurrentUser current, AuditService audit, AppDbContext db, ConnectionTracker connections) =>
        {
            if (id == current.Id)
            {
                throw new AppException(StatusCodes.Status409Conflict, "self_deactivate", "نمی‌توانید حساب خودتان را غیرفعال کنید.");
            }

            var user = await users.FindByIdAsync(id.ToString()) ?? throw AppException.NotFound();
            user.IsActive = false;
            await users.UpdateAsync(user);
            await users.UpdateSecurityStampAsync(user);

            audit.Add("UserDeactivated", "User", user.Id.ToString());
            await db.SaveChangesAsync();
            connections.Disconnect(id);
            return Results.NoContent();
        }).RequirePermission("users.manage");

        group.MapPost("/{id:long}/reset-password", async (long id, ResetPasswordRequest request, UserManager<AppUser> users, AuditService audit, AppDbContext db, ConnectionTracker connections) =>
        {
            var user = await users.FindByIdAsync(id.ToString()) ?? throw AppException.NotFound();
            var token = await users.GeneratePasswordResetTokenAsync(user);
            var result = await users.ResetPasswordAsync(user, token, request.NewPassword);
            if (!result.Succeeded)
            {
                throw ToAppException(result);
            }

            // Clears any lockout, so a locked-out employee can sign in with the new password.
            await users.SetLockoutEndDateAsync(user, null);
            await users.ResetAccessFailedCountAsync(user);
            audit.Add("PasswordReset", "User", user.Id.ToString());
            await db.SaveChangesAsync();
            connections.Disconnect(id);
            return Results.NoContent();
        }).RequirePermission("users.manage");

        group.MapPost("/{id:long}/activate", async (long id, UserManager<AppUser> users, AuditService audit, AppDbContext db) =>
        {
            var user = await users.FindByIdAsync(id.ToString()) ?? throw AppException.NotFound();
            user.IsActive = true;
            await users.UpdateAsync(user);

            audit.Add("UserActivated", "User", user.Id.ToString());
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequirePermission("users.manage");
    }

    private static void EnsureKnownRoles(IEnumerable<string> roles)
    {
        if (roles.Except(RolePermissions.RoleNames).Any())
        {
            throw new AppException(StatusCodes.Status400BadRequest, "unknown_role", "نقش انتخاب‌شده معتبر نیست.");
        }
    }

    private static AppException ToAppException(IdentityResult result)
    {
        var duplicate = result.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.DuplicateUserName));
        return duplicate
            ? new AppException(StatusCodes.Status409Conflict, "duplicate_user", "این نام کاربری قبلاً ثبت شده است.")
            : new AppException(StatusCodes.Status400BadRequest, "weak_password", "رمز عبور شرایط لازم را ندارد.");
    }
}
