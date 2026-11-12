namespace AlShifa.Api.Infrastructure;

/// <summary>Serves the built web app from wwwroot, so one process and one HTTPS port serve the whole hospital.</summary>
public static class FrontendHosting
{
    public const string ContentSecurityPolicy =
        "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; connect-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'";

    private static readonly StaticFileOptions Files = new()
    {
        // Built assets have a hash in their name. Everything else keeps its name, so it must be revalidated after an update.
        OnPrepareResponse = c => c.Context.Response.Headers.CacheControl =
            c.Context.Request.Path.StartsWithSegments("/assets") ? "public, max-age=31536000, immutable" : "no-cache",
    };

    /// <summary>
    /// Compresses the web app's own text files (scripts, styles, the page, icons). API answers are left out on purpose:
    /// compressing secrets next to text a page can influence over HTTPS invites BREACH-style guessing, and on the
    /// hospital network they are small enough already.
    /// </summary>
    public static void AddFrontendCompression(this IServiceCollection services)
    {
        services.AddResponseCompression(o =>
        {
            o.EnableForHttps = true;
            o.MimeTypes = ["text/html", "text/css", "text/javascript", "application/javascript", "image/svg+xml", "font/ttf", "application/vnd.ms-fontobject"];
            o.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
            o.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
        });
    }

    public static void UseFrontendFiles(this WebApplication app)
    {
        // Only files served from wwwroot pass through compression; /api and /hubs never reach it.
        app.UseWhen(c => !c.Request.Path.StartsWithSegments("/api") && !c.Request.Path.StartsWithSegments("/hubs"), b => b.UseResponseCompression());
        app.UseStaticFiles(Files);
    }

    public static void MapFrontendFallback(this WebApplication app)
    {
        // Without these an unknown API path would answer 200 with the web page instead of a clear 404.
        app.MapFallback("/api/{**rest}", () => Results.NotFound());
        app.MapFallback("/hubs/{**rest}", () => Results.NotFound());
        app.MapFallbackToFile("index.html", Files);
    }
}
