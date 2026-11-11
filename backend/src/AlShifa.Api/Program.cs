using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Calls;
using AlShifa.Api.Features.Clinical;
using AlShifa.Api.Features.Finance;
using AlShifa.Api.Features.Inventory;
using AlShifa.Api.Features.Patients;
using AlShifa.Api.Features.Realtime;
using AlShifa.Api.Features.Reports;
using AlShifa.Api.Features.Settings;
using AlShifa.Api.Features.Sync;
using AlShifa.Api.Features.Users;
using AlShifa.Api.Features.Visits;
using AlShifa.Api.Infrastructure;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting.WindowsServices;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // A Windows service starts in System32, so its files must be found relative to the executable.
    ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null,
});

builder.Host.UseWindowsService(o => o.ServiceName = "AlShifaApi");

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured.");

builder.Services.AddDbContext<AppDbContext>(o => o.UseMySQL(connectionString));

// The hospital server records changes for the online copy; the online copy (Mirror:Enabled) only shows them.
var syncSwitch = SyncSwitch.From(builder.Configuration);
builder.Services.AddSingleton(syncSwitch);
builder.Services.Configure<SyncOptions>(builder.Configuration.GetSection("Sync"));
builder.Services.Configure<MirrorOptions>(builder.Configuration.GetSection("Mirror"));

var identity = builder.Services
    .AddIdentity<AppUser, AppRole>(o =>
    {
        o.Password.RequiredLength = 1;
        o.Password.RequireDigit = false;
        o.Password.RequireLowercase = false;
        o.Password.RequireUppercase = false;
        o.Password.RequireNonAlphanumeric = false;
        o.User.RequireUniqueEmail = false;
        o.Lockout.MaxFailedAccessAttempts = 5;
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(10);
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddSignInManager<AppSignInManager>()
    .AddDefaultTokenProviders();
if (syncSwitch.ReadOnly)
{
    identity.AddClaimsPrincipalFactory<MirrorClaimsPrincipalFactory>();
}

// Re-validated on every request: role changes and deactivation take effect immediately.
builder.Services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.Zero);

builder.Services.ConfigureApplicationCookie(o =>
{
    o.Cookie.Name = "alshifa.auth";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = builder.Environment.IsProduction() ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
    o.ExpireTimeSpan = TimeSpan.FromHours(10);
    o.SlidingExpiration = true;
    // Set handlers individually: replacing o.Events would drop Identity's security stamp validation.
    o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
});

// Keys protect the auth cookie. If they do not survive a restart, every user is signed out by each deploy.
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("AlShifa");
if (builder.Configuration["DataProtection:KeysPath"] is { Length: > 0 } keysPath)
{
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
}

builder.Services.AddAntiforgery(o =>
{
    o.HeaderName = "X-XSRF-TOKEN";
    o.Cookie.Name = "alshifa.csrf";
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = builder.Environment.IsProduction() ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
});

builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
builder.Services.AddSingleton<IAuthorizationHandler, PermissionHandler>();
builder.Services.AddAuthorization();

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("login", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = builder.Configuration.GetValue("RateLimit:LoginPerMinute", 60), Window = TimeSpan.FromMinutes(1) }));
});

// Behind Nginx the client address arrives in a header. Only the local proxy is trusted to set it,
// otherwise every user would share one rate-limit bucket and audit logs would show the proxy's address.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);

builder.Services.AddHttpContextAccessor();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddValidation();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database");

builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<AuditService>();
builder.Services.AddSingleton<IBackupTrigger, EventLogBackupTrigger>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<NumberService>();
builder.Services.AddScoped<Outbox>();
builder.Services.AddScoped<Idempotency>();
builder.Services.AddScoped<VisitService>();
builder.Services.AddScoped<VisitFeeService>();
builder.Services.AddScoped<ClinicalAccess>();
builder.Services.AddScoped<LedgerService>();
builder.Services.AddScoped<InvoiceService>();
builder.Services.AddScoped<StockService>();
builder.Services.AddScoped<SaleService>();

builder.Services.AddSignalR();
builder.Services.AddSingleton<IUserIdProvider, UserIdFromNameIdentifier>();
builder.Services.AddSingleton<ConnectionTracker>();
if (syncSwitch.ReadOnly)
{
    // The hospital server sends its batches compressed.
    builder.Services.AddRequestDecompression();
}
else
{
    builder.Services.AddHostedService<OutboxDispatcher>();
}

// A failing background task (sync, live updates) is logged and must never take the hospital application down with it.
builder.Services.Configure<HostOptions>(o => o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

if (syncSwitch.Capture)
{
    builder.Services.AddHttpClient(SyncWorker.HttpClientName);
    builder.Services.AddSingleton<SyncWorker>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<SyncWorker>());
}
else if (!syncSwitch.ReadOnly)
{
    builder.Services.AddHostedService<SyncOffMarker>();
}

var app = builder.Build();

// Operator commands: never run implicitly in production.
if (args.Contains("--migrate") || args.Contains("--create-admin"))
{
    using var scope = app.Services.CreateScope();
    var operatorDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await DatabaseSafety.EnsureOperatorMayMigrateAsync(operatorDb, args.Contains(DatabaseSafety.BackupTakenFlag));
    await operatorDb.Database.MigrateAsync();

    // Online, every row including roles and reference data comes from the hospital server; seeded rows would collide with them.
    if (!syncSwitch.ReadOnly)
    {
        await Seeder.SeedReferenceDataAsync(scope.ServiceProvider);
    }

    if (args.Contains("--create-admin"))
    {
        var userName = Environment.GetEnvironmentVariable("ALSHIFA_ADMIN_USER");
        var password = Environment.GetEnvironmentVariable("ALSHIFA_ADMIN_PASSWORD");
        if (string.IsNullOrEmpty(userName) || string.IsNullOrEmpty(password))
        {
            throw new InvalidOperationException("Set ALSHIFA_ADMIN_USER and ALSHIFA_ADMIN_PASSWORD.");
        }

        await Seeder.CreateAdminAsync(scope.ServiceProvider, userName, password);
    }

    return;
}

// Operator command: opening stock from the clinic's counted sheet. Dry run unless --apply is given.
if (args.Contains(OpeningStockImport.Command))
{
    var position = Array.IndexOf(args, OpeningStockImport.Command) + 1;
    var path = position < args.Length ? args[position] : throw new InvalidOperationException($"Usage: {OpeningStockImport.Command} <inventory_staging.json> [--apply --backup-taken]");
    using var scope = app.Services.CreateScope();
    string? After(string flag) => Array.IndexOf(args, flag) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    if (OpeningStockSheet.Handles(path))
    {
        // A typed sheet (.xlsx/.csv). The launcher in deploy\stock-import passes the preview's count, so the apply imports exactly what was shown.
        await OpeningStockSheet.RunAsync(scope.ServiceProvider, path, Environment.GetEnvironmentVariable("ALSHIFA_IMPORT_USER"),
            args.Contains(OpeningStockImport.ApplyFlag), args.Contains(DatabaseSafety.BackupTakenFlag),
            int.TryParse(After(OpeningStockSheet.ExpectFlag), out var expect) ? expect : null, After(OpeningStockSheet.ReportFlag), Console.Out);
    }
    else
    {
        await OpeningStockImport.RunAsync(scope.ServiceProvider, path, Environment.GetEnvironmentVariable("ALSHIFA_IMPORT_USER"),
            args.Contains(OpeningStockImport.ApplyFlag), args.Contains(DatabaseSafety.BackupTakenFlag), Console.Out);
    }

    return;
}

// Operator command on the online server: lets one synced account sign in online, with its own password and an authenticator app.
if (args.Contains("--online-access") || args.Contains("--online-revoke"))
{
    using var scope = app.Services.CreateScope();
    await OnlineAccess.RunAsync(scope.ServiceProvider, revoke: args.Contains("--online-revoke"));
    return;
}

using (var scope = app.Services.CreateScope())
{
    var startupDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    // The environment name alone is not enough: a wrong setting on the host must not change a real database.
    // The online copy runs on a reading database account; its schema is migrated by the deployment, never here.
    if ((app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing")) && DatabaseSafety.IsDisposable(connectionString) && !syncSwitch.ReadOnly)
    {
        await startupDb.Database.MigrateAsync();
        await Seeder.SeedReferenceDataAsync(scope.ServiceProvider);
    }
    else if (await startupDb.Database.CanConnectAsync())
    {
        if (await DatabaseSafety.SchemaProblemAsync(startupDb) is { } problem)
        {
            app.Logger.LogCritical("Not starting: {Problem}", problem);
            Environment.ExitCode = 1;
            return;
        }
    }
    else
    {
        // MySQL can still be starting after a reboot. /health reports this until it answers.
        app.Logger.LogWarning("The database is not reachable yet, so its schema could not be checked at startup.");
    }
}

// On the hospital host there is no separate web server: the published web app sits in wwwroot next to the API.
var hostsFrontend = app.Environment.WebRootFileProvider.GetFileInfo("index.html").Exists;

app.UseForwardedHeaders();
app.UseExceptionHandler();

app.Use((http, next) =>
{
    http.Response.Headers.XContentTypeOptions = "nosniff";
    http.Response.Headers.XFrameOptions = "DENY";
    http.Response.Headers["Referrer-Policy"] = "no-referrer";
    if (hostsFrontend)
    {
        http.Response.Headers.ContentSecurityPolicy = FrontendHosting.ContentSecurityPolicy;
    }

    // Patient data must not stay in shared browser or proxy caches.
    if (http.Request.Path.StartsWithSegments("/api"))
    {
        http.Response.Headers.CacheControl = "no-store";
    }

    return next();
});

if (app.Environment.IsProduction())
{
    app.UseHsts();
}

if (syncSwitch.ReadOnly)
{
    app.UseReadOnlyMirror();
    app.UseRequestDecompression();
}

if (hostsFrontend)
{
    app.UseFrontendFiles();
}

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// CSRF check for every state-changing API call; /api/auth/csrf is a GET so a client can always obtain a token.
app.Use(async (http, next) =>
{
    var method = http.Request.Method;
    var unsafeMethod = !(HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method));
    // The online copy's sync endpoints carry no cookie; each request is signed by the hospital server instead.
    var signed = syncSwitch.ReadOnly && http.Request.Path.StartsWithSegments("/api/sync/mirror");
    if (unsafeMethod && !signed && http.Request.Path.StartsWithSegments("/api"))
    {
        var antiforgery = http.RequestServices.GetRequiredService<IAntiforgery>();
        if (!await antiforgery.IsRequestValidAsync(http))
        {
            throw new AppException(StatusCodes.Status400BadRequest, "csrf_invalid", "نشست منقضی شده است. صفحه را تازه کنید.");
        }
    }

    await next();
});

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapHealthChecks("/health");
app.MapHub<EventsHub>(EventsHub.Path);
app.MapAuth();
app.MapUsers();
app.MapStaff();
app.MapDirectories();
app.MapPatients();
app.MapVisits();
app.MapCalls();
app.MapReceptionFees();
app.MapClinical();
app.MapRefractions();
app.MapPrescriptions();
app.MapDiagnostics();
app.MapSurgeries();
app.MapSurgeryPackages();
app.MapFinance();
app.MapInventory();
app.MapPharmacy();
app.MapSettings();
app.MapReports();
app.MapPrint();
app.MapAudit();
app.MapSyncStatus(syncSwitch.ReadOnly);
if (syncSwitch.ReadOnly)
{
    app.MapMirror();
}

if (hostsFrontend)
{
    app.MapFrontendFallback();
}

app.Run();

public partial class Program;
