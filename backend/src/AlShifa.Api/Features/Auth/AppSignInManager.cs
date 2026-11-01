using AlShifa.Api.Features.Users;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace AlShifa.Api.Features.Auth;

/// <summary>Rejects deactivated accounts at sign-in and on every later cookie validation.</summary>
public sealed class AppSignInManager(
    UserManager<AppUser> userManager,
    IHttpContextAccessor contextAccessor,
    IUserClaimsPrincipalFactory<AppUser> claimsFactory,
    IOptions<IdentityOptions> optionsAccessor,
    ILogger<SignInManager<AppUser>> logger,
    IAuthenticationSchemeProvider schemes,
    IUserConfirmation<AppUser> confirmation)
    : SignInManager<AppUser>(userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
{
    public override async Task<bool> CanSignInAsync(AppUser user) =>
        user.IsActive && await base.CanSignInAsync(user);

    public override async Task<AppUser?> ValidateSecurityStampAsync(System.Security.Claims.ClaimsPrincipal? principal)
    {
        var user = await base.ValidateSecurityStampAsync(principal);
        return user is { IsActive: true } ? user : null;
    }
}
