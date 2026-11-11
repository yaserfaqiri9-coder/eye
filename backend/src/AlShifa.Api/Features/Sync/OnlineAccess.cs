using AlShifa.Api.Features.Users;
using Microsoft.AspNetCore.Identity;

namespace AlShifa.Api.Features.Sync;

/// <summary>
/// Run on the online server only. Accounts arrive from the hospital without a password, so none can sign in online
/// until this gives one a password of its own and an authenticator app (two-step sign-in).
///   ALSHIFA_ONLINE_USER=director ALSHIFA_ONLINE_PASSWORD=... ./AlShifa.Api --online-access
///   ALSHIFA_ONLINE_USER=director ./AlShifa.Api --online-revoke
/// It needs the writing database account: ConnectionStrings__Default set to Mirror:SyncConnection for this one command.
/// </summary>
public static class OnlineAccess
{
    private const int MinPasswordLength = 12;

    public static async Task RunAsync(IServiceProvider services, bool revoke)
    {
        var sync = services.GetRequiredService<SyncSwitch>();
        if (!sync.ReadOnly)
        {
            throw new InvalidOperationException("Online access is set on the online server only (Mirror:Enabled=true).");
        }

        sync.OperatorWrites = true;

        var users = services.GetRequiredService<UserManager<AppUser>>();
        var userName = Environment.GetEnvironmentVariable("ALSHIFA_ONLINE_USER");
        var user = (string.IsNullOrWhiteSpace(userName) ? null : await users.FindByNameAsync(userName))
            ?? throw new InvalidOperationException("Set ALSHIFA_ONLINE_USER to an account that has reached the online copy through sync.");

        if (revoke)
        {
            Check(await users.SetTwoFactorEnabledAsync(user, false));
            if (await users.HasPasswordAsync(user))
            {
                Check(await users.RemovePasswordAsync(user));
            }

            Check(await users.UpdateSecurityStampAsync(user));
            Console.WriteLine($"Online access removed for {user.UserName}.");
            return;
        }

        var password = Environment.GetEnvironmentVariable("ALSHIFA_ONLINE_PASSWORD");
        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength)
        {
            throw new InvalidOperationException($"Set ALSHIFA_ONLINE_PASSWORD to at least {MinPasswordLength} characters. It is used online only.");
        }

        if (await users.HasPasswordAsync(user))
        {
            Check(await users.RemovePasswordAsync(user));
        }

        Check(await users.AddPasswordAsync(user, password));
        Check(await users.SetLockoutEnabledAsync(user, true));
        Check(await users.ResetAuthenticatorKeyAsync(user));
        Check(await users.SetTwoFactorEnabledAsync(user, true));
        var key = await users.GetAuthenticatorKeyAsync(user);

        // Shown once, to the operator at the server console. Scan the link as a QR code or type the key into the authenticator app.
        Console.WriteLine($"Online access enabled for {user.UserName}.");
        Console.WriteLine($"Authenticator key: {key}");
        Console.WriteLine($"otpauth://totp/AlShifa:{Uri.EscapeDataString(user.UserName!)}?secret={key}&issuer=AlShifa&digits=6");
    }

    private static void Check(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(string.Join("; ", result.Errors.Select(e => e.Description)));
        }
    }
}
