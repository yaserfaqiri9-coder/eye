using System.ComponentModel.DataAnnotations;
using AlShifa.Api.Features.Users;
using AlShifa.Api.Infrastructure;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;

namespace AlShifa.Api.Features.Auth;

public sealed record LoginRequest([Required, MaxLength(100)] string UserName, [Required, MaxLength(200)] string Password);

public sealed record ChangePasswordRequest([Required, MaxLength(200)] string CurrentPassword, [Required, MaxLength(200)] string NewPassword);

public sealed record CurrentUserDto(long Id, string UserName, string FullName, IReadOnlyList<string> Roles, IReadOnlyList<string> Permissions);

public static class AuthEndpoints
{
    public const string XsrfCookie = "XSRF-TOKEN";

    public static void MapAuth(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        group.MapGet("/csrf", (HttpContext http, IAntiforgery antiforgery) =>
        {
            IssueXsrfCookie(http, antiforgery);
            return Results.NoContent();
        });

        group.MapPost("/login", async (
            LoginRequest request,
            HttpContext http,
            SignInManager<AppUser> signIn,
            UserManager<AppUser> users,
            IAntiforgery antiforgery,
            AuditService audit,
            AppDbContext db) =>
        {
            var user = await users.FindByNameAsync(request.UserName);
            var result = user is null
                ? Microsoft.AspNetCore.Identity.SignInResult.Failed
                : await signIn.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);

            // IsNotAllowed (deactivated) and bad credentials share one message so accounts cannot be probed.
            if (!result.Succeeded)
            {
                audit.Add("LoginFailed", "User", user?.Id.ToString(), RemoteAccess.Email(http) is { } email ? $"Online, Cloudflare Access: {email}" : null);
                await db.SaveChangesAsync();
                var message = result.IsLockedOut ? "حساب به‌طور موقت قفل شده است. بعداً تلاش کنید." : "نام کاربری یا رمز درست نیست.";
                throw new AppException(StatusCodes.Status401Unauthorized, "invalid_login", message);
            }

            // Checked after the password, so a refusal says nothing about accounts the caller cannot sign in to.
            var remoteEmail = RemoteAccess.Email(http);
            if (RemoteAccess.IsRemote(http) && !RemoteAccess.MayUse(await signIn.CreateUserPrincipalAsync(user!)))
            {
                audit.Add("RemoteLoginDenied", "User", user!.Id.ToString(), $"Cloudflare Access: {remoteEmail}", userId: user.Id);
                await db.SaveChangesAsync();
                throw new AppException(StatusCodes.Status403Forbidden, "remote_not_allowed", "این حساب برای دسترسی آنلاین مجاز نیست.");
            }

            await signIn.SignInAsync(user!, isPersistent: false);
            audit.Add("Login", "User", user!.Id.ToString(), remoteEmail is null ? null : $"Online, Cloudflare Access: {remoteEmail}", userId: user.Id);
            await db.SaveChangesAsync();

            // The antiforgery token is bound to the identity, so it must be reissued after login.
            http.User = await signIn.CreateUserPrincipalAsync(user);
            IssueXsrfCookie(http, antiforgery);
            return Results.Ok(ToDto(user, http));
        }).RequireRateLimiting("login");

        group.MapPost("/logout", async (HttpContext http, SignInManager<AppUser> signIn, IAntiforgery antiforgery) =>
        {
            await signIn.SignOutAsync();
            http.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity());
            IssueXsrfCookie(http, antiforgery);
            return Results.NoContent();
        }).RequireAuthorization();

        group.MapPost("/change-password", async (ChangePasswordRequest request, HttpContext http, UserManager<AppUser> users, SignInManager<AppUser> signIn, IAntiforgery antiforgery, AuditService audit, AppDbContext db) =>
        {
            var user = await users.GetUserAsync(http.User) ?? throw new AppException(StatusCodes.Status401Unauthorized, "unauthenticated", "ابتدا وارد شوید.");
            var result = await users.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
            if (!result.Succeeded)
            {
                var wrong = result.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.PasswordMismatch));
                throw new AppException(StatusCodes.Status400BadRequest, wrong ? "wrong_password" : "weak_password", wrong ? "رمز فعلی درست نیست." : "رمز جدید شرایط لازم را ندارد.");
            }

            // The stamp changed, which ends every other session; this one continues with a fresh cookie.
            await signIn.RefreshSignInAsync(user);
            audit.Add("PasswordChanged", "User", user.Id.ToString());
            await db.SaveChangesAsync();
            http.User = await signIn.CreateUserPrincipalAsync(user);
            IssueXsrfCookie(http, antiforgery);
            return Results.NoContent();
        }).RequireAuthorization();

        group.MapGet("/me", async (HttpContext http, UserManager<AppUser> users) =>
        {
            var user = await users.GetUserAsync(http.User);
            return user is null ? Results.Unauthorized() : Results.Ok(ToDto(user, http));
        }).RequireAuthorization();
    }

    private static void IssueXsrfCookie(HttpContext http, IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(http);
        http.Response.Cookies.Append(XsrfCookie, tokens.RequestToken!, new CookieOptions
        {
            HttpOnly = false,
            SameSite = SameSiteMode.Strict,
            Secure = http.Request.IsHttps,
            Path = "/",
        });
    }

    private static CurrentUserDto ToDto(AppUser user, HttpContext http) => new(
        user.Id,
        user.UserName!,
        user.FullName,
        http.User.FindAll(System.Security.Claims.ClaimTypes.Role).Select(c => c.Value).Distinct().ToList(),
        http.User.FindAll(RolePermissions.ClaimType).Select(c => c.Value).Distinct().Order().ToList());
}
