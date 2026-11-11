using System.ComponentModel.DataAnnotations;
using AlShifa.Api.Features.Sync;
using AlShifa.Api.Features.Users;
using AlShifa.Api.Infrastructure;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;

namespace AlShifa.Api.Features.Auth;

/// <param name="Code">The six digits from the authenticator app. Asked for online only.</param>
public sealed record LoginRequest([Required, MaxLength(100)] string UserName, [Required, MaxLength(200)] string Password, [MaxLength(10)] string? Code = null);

public sealed record ChangePasswordRequest([Required, MaxLength(200)] string CurrentPassword, [Required, MaxLength(200)] string NewPassword);

public sealed record CurrentUserDto(long Id, string UserName, string FullName, IReadOnlyList<string> Roles, IReadOnlyList<string> Permissions, IReadOnlyList<string> HiddenMenu, bool ReadOnly = false);

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
            AppDbContext db,
            SyncSwitch sync) =>
        {
            var user = await users.FindByNameAsync(request.UserName);
            var result = user is null
                ? Microsoft.AspNetCore.Identity.SignInResult.Failed
                : await signIn.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);

            // IsNotAllowed (deactivated) and bad credentials share one message so accounts cannot be probed.
            if (!result.Succeeded)
            {
                audit.Add("LoginFailed", "User", user?.Id.ToString());
                await db.SaveChangesAsync();
                var message = result.IsLockedOut ? "حساب به‌طور موقت قفل شده است. بعداً تلاش کنید." : "نام کاربری یا رمز درست نیست.";
                throw new AppException(StatusCodes.Status401Unauthorized, "invalid_login", message);
            }

            // Online, a password alone is not enough: the account must have been enabled there with an authenticator app.
            if (sync.ReadOnly)
            {
                if (!user!.TwoFactorEnabled)
                {
                    audit.Add("OnlineLoginDenied", "User", user.Id.ToString(), "Online access not enabled", userId: user.Id);
                    throw new AppException(StatusCodes.Status403Forbidden, "online_not_enabled", "این حساب برای ورود آنلاین فعال نشده است.");
                }

                if (string.IsNullOrWhiteSpace(request.Code))
                {
                    throw new AppException(StatusCodes.Status401Unauthorized, "two_factor_required", "کد شش‌رقمی برنامه تأیید هویت را وارد کنید.");
                }

                var code = request.Code.Replace(" ", "").Replace("-", "");
                if (!await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, code))
                {
                    await users.AccessFailedAsync(user);
                    audit.Add("LoginFailed", "User", user.Id.ToString(), "Wrong authenticator code", userId: user.Id);
                    throw new AppException(StatusCodes.Status401Unauthorized, "invalid_code", "کد تأیید درست نیست یا منقضی شده است.");
                }
            }

            await signIn.SignInAsync(user!, isPersistent: false);
            audit.Add("Login", "User", user!.Id.ToString(), sync.ReadOnly ? "Online" : null, userId: user.Id);
            await db.SaveChangesAsync();

            // The antiforgery token is bound to the identity, so it must be reissued after login.
            http.User = await signIn.CreateUserPrincipalAsync(user);
            IssueXsrfCookie(http, antiforgery);
            return Results.Ok(await ToDto(user, http, users, sync));
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

        group.MapGet("/me", async (HttpContext http, UserManager<AppUser> users, SyncSwitch sync) =>
        {
            var user = await users.GetUserAsync(http.User);
            return user is null ? Results.Unauthorized() : Results.Ok(await ToDto(user, http, users, sync));
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

    // Hidden sidebar pages come from the database, so a change shows on the user's next page load without signing in again.
    private static async Task<CurrentUserDto> ToDto(AppUser user, HttpContext http, UserManager<AppUser> users, SyncSwitch sync) => new(
        user.Id,
        user.UserName!,
        user.FullName,
        http.User.FindAll(System.Security.Claims.ClaimTypes.Role).Select(c => c.Value).Distinct().ToList(),
        http.User.FindAll(RolePermissions.ClaimType).Select(c => c.Value).Distinct().Order().ToList(),
        (await users.GetClaimsAsync(user)).Where(c => c.Type == Sidebar.HiddenClaim).Select(c => c.Value).Order().ToList(),
        sync.ReadOnly);
}
