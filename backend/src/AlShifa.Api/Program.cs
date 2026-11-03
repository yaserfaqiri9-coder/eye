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

builder.Services
    .AddIdentity<AppUser, AppRole>(o =>
    {
        o.Password.RequiredLength = 10;
        o.Password.RequireNonAlphanumeric = false;
        o.User.RequireUniqueEmail = false;
        o.Lockout.MaxFailedAccessAttempts = 5;
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(10);
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddSignInManager<AppSignInManager>()
    .AddDefaultTokenProviders();

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
builder.Services.AddHostedService<OutboxDispatcher>();

var app = builder.Build();

// Operator commands: never run implicitly in production.
if (args.Contains("--migrate") || args.Contains("--create-admin"))
{
    using var scope = app.Services.CreateScope();
    var operatorDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await DatabaseSafety.EnsureOperatorMayMigrateAsync(operatorDb, args.Contains(DatabaseSafety.BackupTakenFlag));
    await operatorDb.Database.MigrateAsync();
    await Seeder.SeedReferenceDataAsync(scope.ServiceProvider);

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

using (var scope = app.Services.CreateScope())
{
    var startupDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    // The environment name alone is not enough: a wrong setting on the host must not change a real database.
    if ((app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing")) && DatabaseSafety.IsDisposable(connectionString))
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
    if (unsafeMethod && http.Request.Path.StartsWithSegments("/api"))
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
app.MapPatients();
app.MapVisits();
app.MapCalls();
app.MapReceptionFees();
app.MapClinical();
app.MapRefractions();
app.MapPrescriptions();
app.MapDiagnostics();
app.MapSurgeries();
app.MapFinance();
app.MapInventory();
app.MapSettings();
app.MapReports();
app.MapPrint();
app.MapAudit();
app.MapSystemStatus();

if (hostsFrontend)
{
    app.MapFrontendFallback();
}

app.Run();

public partial class Program;
