using System.Buffers.Text;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AlShifa.Api.Features.Auth;

namespace AlShifa.Api.Infrastructure;

/// <summary>
/// Online access to reports through Cloudflare Tunnel and Cloudflare Access (see DEPLOYMENT-WINDOWS.md section 7).
/// cloudflared is the only program that connects to the loopback port in RemoteAccess:Port. Every request arriving there must carry
/// a Cloudflare Access token for this application, may reach only the report pages, and must belong to a DoctorReportsViewer account.
/// The hospital network never passes through here, so none of this can slow down or stop work inside the hospital.
/// </summary>
public sealed class RemoteAccessOptions
{
    /// <summary>0 means online access is not set up.</summary>
    public int Port { get; set; }

    /// <summary>The Zero Trust team domain, for example alshifa.cloudflareaccess.com.</summary>
    public string TeamDomain { get; set; } = "";

    /// <summary>The Application Audience (AUD) tag of the Access application.</summary>
    public string Audience { get; set; } = "";
}

public static class RemoteAccess
{
    public const string TokenHeader = "Cf-Access-Jwt-Assertion";
    private const string EmailItem = "RemoteAccessEmail";

    /// <summary>What a report viewer needs online. Everything else answers 404, as if it did not exist.</summary>
    private static readonly string[] ReportPaths =
        ["/api/reports/dashboard", "/api/reports/overview", "/api/reports/patients", "/api/reports/doctors", "/api/reports/diagnostics", "/api/reports/surgeries", "/api/reports/diseases"];

    public static bool IsRemote(HttpContext http, RemoteAccessOptions options) =>
        options.Port > 0 && http.Connection.LocalPort == options.Port && http.Connection.LocalIpAddress is { } local && IPAddress.IsLoopback(local);

    public static bool IsRemote(HttpContext http) => http.Items.ContainsKey(EmailItem);

    /// <summary>The Cloudflare Access identity of a remote request, for the audit log.</summary>
    public static string? Email(HttpContext http) => http.Items.TryGetValue(EmailItem, out var email) ? email as string : null;

    /// <summary>Online accounts are dedicated: another role would bring permissions (money, clinical records) that must stay inside the hospital.</summary>
    public static bool MayUse(ClaimsPrincipal user) =>
        user.FindAll(ClaimTypes.Role).Select(c => c.Value).Distinct().ToList() is [Roles.DoctorReportsViewer];

    private static bool PathAllowed(HttpRequest request)
    {
        var path = request.Path;
        var reading = HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method);
        if (path.StartsWithSegments("/api"))
        {
            return path.Equals("/api/auth/login") || path.Equals("/api/auth/logout") || path.Equals("/api/auth/change-password")
                ? HttpMethods.IsPost(request.Method)
                : reading && (path.Equals("/api/auth/csrf") || path.Equals("/api/auth/me") || path.Equals("/api/settings") || path.Equals("/api/settings/")
                    || ReportPaths.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase)));
        }

        // The live connection only joins groups the account's permissions allow, and a report viewer has none.
        if (path.StartsWithSegments("/hubs/events"))
        {
            return true;
        }

        // The web application's own files.
        return reading && !path.StartsWithSegments("/health") && !path.StartsWithSegments("/openapi");
    }

    /// <summary>Runs before anything else can answer: the Access token, then the list of allowed paths.</summary>
    public static IApplicationBuilder UseRemoteAccessGate(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        var options = http.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<RemoteAccessOptions>>().Value;
        if (!IsRemote(http, options))
        {
            await next();
            return;
        }

        var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("AlShifa.RemoteAccess");
        var validator = http.RequestServices.GetRequiredService<AccessTokenValidator>();
        var result = await validator.ValidateAsync(http.Request.Headers[TokenHeader].ToString(), http.RequestAborted);
        if (result.Email is null)
        {
            // Warnings reach the Windows Application log, the security record kept on the host.
            logger.LogWarning("Remote request refused: {Reason}. {Method} {Path} from {Address}", result.Problem, http.Request.Method, http.Request.Path, http.Connection.RemoteIpAddress);
            http.Response.StatusCode = result.Unavailable ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status403Forbidden;
            return;
        }

        http.Items[EmailItem] = result.Email;

        // The browser spoke HTTPS to Cloudflare; only the last hop on this computer is plain HTTP. Secure cookies and
        // antiforgery need to know that. The visitor's address is the one Cloudflare saw, for the audit log and the login limit.
        http.Request.Scheme = "https";
        if (IPAddress.TryParse(http.Request.Headers["Cf-Connecting-IP"].ToString(), out var visitor))
        {
            http.Connection.RemoteIpAddress = visitor;
        }

        if (!PathAllowed(http.Request))
        {
            logger.LogWarning("Remote request outside reports refused: {Method} {Path} by {Email}", http.Request.Method, http.Request.Path, result.Email);
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await next();
    });

    /// <summary>After authentication: a session that is not a report viewer's is refused, however it was obtained.</summary>
    public static IApplicationBuilder UseRemoteAccessUserCheck(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        if (IsRemote(http) && http.User.Identity?.IsAuthenticated == true && !MayUse(http.User))
        {
            var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("AlShifa.RemoteAccess");
            logger.LogWarning("Remote session of a non-viewer account refused: user {User}, {Path}, Access identity {Email}", http.User.Identity.Name, http.Request.Path, Email(http));
            throw new AppException(StatusCodes.Status403Forbidden, "remote_not_allowed", "این حساب برای دسترسی آنلاین مجاز نیست.");
        }

        await next();
    });
}

/// <summary>Public keys Cloudflare signs Access tokens with, from https://&lt;team&gt;/cdn-cgi/access/certs.</summary>
public interface IAccessKeySource
{
    Task<IReadOnlyDictionary<string, RSA>> GetKeysAsync(bool refresh, CancellationToken ct);
}

public sealed class CloudflareAccessKeySource(IHttpClientFactory http, Microsoft.Extensions.Options.IOptions<RemoteAccessOptions> options) : IAccessKeySource
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan MinRefreshInterval = TimeSpan.FromMinutes(1);
    private readonly SemaphoreSlim gate = new(1, 1);
    private IReadOnlyDictionary<string, RSA> keys = new Dictionary<string, RSA>();
    private DateTime fetchedAt = DateTime.MinValue;

    public async Task<IReadOnlyDictionary<string, RSA>> GetKeysAsync(bool refresh, CancellationToken ct)
    {
        var age = DateTime.UtcNow - fetchedAt;
        // Cloudflare rotates its keys; an unknown key id may refetch, but never more than once a minute.
        if (age < Lifetime && !(refresh && age > MinRefreshInterval))
        {
            return keys;
        }

        await gate.WaitAsync(ct);
        try
        {
            age = DateTime.UtcNow - fetchedAt;
            if (age < Lifetime && !(refresh && age > MinRefreshInterval))
            {
                return keys;
            }

            using var client = http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            using var document = JsonDocument.Parse(await client.GetStringAsync($"https://{options.Value.TeamDomain}/cdn-cgi/access/certs", ct));
            var loaded = new Dictionary<string, RSA>();
            foreach (var key in document.RootElement.GetProperty("keys").EnumerateArray())
            {
                if (key.GetProperty("kty").GetString() != "RSA" || key.GetProperty("kid").GetString() is not { } kid)
                {
                    continue;
                }

                var rsa = RSA.Create();
                rsa.ImportParameters(new RSAParameters
                {
                    Modulus = Base64Url.DecodeFromChars(key.GetProperty("n").GetString()),
                    Exponent = Base64Url.DecodeFromChars(key.GetProperty("e").GetString()),
                });
                loaded[kid] = rsa;
            }

            keys = loaded;
            fetchedAt = DateTime.UtcNow;
            return keys;
        }
        finally
        {
            gate.Release();
        }
    }
}

public sealed record AccessTokenResult(string? Email, string? Problem, bool Unavailable = false);

/// <summary>
/// Checks the token Cloudflare Access adds to every request it lets through (RS256, as documented by Cloudflare).
/// Cloudflare already checked the sign-in; checking again here means a mistake in the Cloudflare settings, or a program on
/// this computer calling the port directly, still cannot reach the application.
/// </summary>
public sealed class AccessTokenValidator(IAccessKeySource keySource, Microsoft.Extensions.Options.IOptions<RemoteAccessOptions> options, TimeProvider clock)
{
    private static readonly TimeSpan Skew = TimeSpan.FromMinutes(1);

    public async Task<AccessTokenResult> ValidateAsync(string token, CancellationToken ct)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.TeamDomain) || string.IsNullOrWhiteSpace(settings.Audience))
        {
            return new AccessTokenResult(null, "RemoteAccess:TeamDomain or RemoteAccess:Audience is not configured", Unavailable: true);
        }

        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            return new AccessTokenResult(null, "no Access token");
        }

        JsonElement header, payload;
        byte[] signature;
        try
        {
            header = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[0])).RootElement;
            payload = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1])).RootElement;
            signature = Base64Url.DecodeFromChars(parts[2]);
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            return new AccessTokenResult(null, "malformed Access token");
        }

        // The algorithm is fixed here, never taken from the token.
        if (Text(header, "alg") != "RS256" || Text(header, "kid") is not { } kid)
        {
            return new AccessTokenResult(null, "unexpected token algorithm");
        }

        RSA? key;
        try
        {
            var keys = await keySource.GetKeysAsync(refresh: false, ct);
            if (!keys.TryGetValue(kid, out key))
            {
                keys = await keySource.GetKeysAsync(refresh: true, ct);
                keys.TryGetValue(kid, out key);
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException or CryptographicException)
        {
            return new AccessTokenResult(null, $"Cloudflare signing keys unavailable ({e.GetType().Name})", Unavailable: true);
        }

        if (key is null)
        {
            return new AccessTokenResult(null, "unknown signing key");
        }

        var signed = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
        if (!key.VerifyData(signed, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
        {
            return new AccessTokenResult(null, "bad token signature");
        }

        var now = clock.GetUtcNow();
        if (Time(payload, "exp") is not { } expires || expires < now - Skew || Time(payload, "nbf") > now + Skew)
        {
            return new AccessTokenResult(null, "token expired or not yet valid");
        }

        if (Text(payload, "iss") != $"https://{settings.TeamDomain}")
        {
            return new AccessTokenResult(null, "token from another Cloudflare team");
        }

        var audience = payload.TryGetProperty("aud", out var aud)
            ? aud.ValueKind == JsonValueKind.Array ? aud.EnumerateArray().Select(a => a.GetString()).ToList() : [aud.GetString()]
            : [];
        if (!audience.Contains(settings.Audience))
        {
            return new AccessTokenResult(null, "token for another Access application");
        }

        // Service tokens have no email. Only people sign in here.
        return Text(payload, "email") is { Length: > 0 } email
            ? new AccessTokenResult(email, null)
            : new AccessTokenResult(null, "token has no user email");
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTimeOffset? Time(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt64(out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
}
