using System.Security.Claims;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MySql.Data.MySqlClient;

namespace AlShifa.Api.Infrastructure;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

public static class Text
{
    /// <summary>Blank form inputs are stored as NULL so "not recorded" stays distinct from any value.</summary>
    public static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class AppException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;

    public string Code { get; } = code;

    public static AppException NotFound() =>
        new(StatusCodes.Status404NotFound, "not_found", "مورد درخواستی پیدا نشد.");
}

public sealed class CurrentUser(IHttpContextAccessor accessor)
{
    public long? IdOrNull =>
        long.TryParse(accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public long Id => IdOrNull ?? throw new AppException(StatusCodes.Status401Unauthorized, "unauthenticated", "ابتدا وارد شوید.");

    public string? IpAddress => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();
}

/// <summary>Adds audit rows to the current unit of work so they commit atomically with the change they describe.</summary>
public sealed class AuditService(AppDbContext db, CurrentUser user)
{
    public void Add(string action, string entity, string? entityId, string? details = null, long? userId = null) =>
        db.AuditLogs.Add(new AuditLog
        {
            UserId = userId ?? user.IdOrNull,
            Action = action,
            Entity = entity,
            EntityId = entityId,
            Details = details,
            IpAddress = user.IpAddress,
        });
}

public sealed class DatabaseHealthCheck(AppDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        if (!await db.Database.CanConnectAsync(ct))
        {
            return HealthCheckResult.Unhealthy();
        }

        return await DatabaseSafety.SchemaProblemAsync(db, ct) is { } problem ? HealthCheckResult.Unhealthy(problem) : HealthCheckResult.Healthy();
    }
}

public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger, IProblemDetailsService problems)
    : IExceptionHandler
{
    private const int MySqlDuplicateEntry = 1062;
    private const int MySqlLockTimeout = 1205;
    private const int MySqlDeadlock = 1213;

    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        var (status, code, message) = exception switch
        {
            AppException app => (app.Status, app.Code, app.Message),
            BadHttpRequestException bad => (bad.StatusCode, "invalid_request", "اطلاعات ارسال‌شده معتبر نیست."),
            DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, "concurrency_conflict",
                "این مورد هم‌زمان توسط کاربر دیگری تغییر کرده است. صفحه را تازه کنید و دوباره بررسی کنید."),
            DbUpdateException { InnerException: MySqlException { Number: MySqlDuplicateEntry } } =>
                (StatusCodes.Status409Conflict, "duplicate", "این مورد قبلاً ثبت شده است."),
            DbUpdateException { InnerException: MySqlException { Number: MySqlDeadlock or MySqlLockTimeout } } or MySqlException { Number: MySqlDeadlock or MySqlLockTimeout } =>
                (StatusCodes.Status409Conflict, "busy", "سیستم مشغول بود و این عملیات ثبت نشد. دوباره تلاش کنید."),
            Features.Patients.PossibleDuplicateException =>
                (StatusCodes.Status409Conflict, "possible_duplicate", "مریض مشابهی قبلاً ثبت شده است. لطفاً بررسی کنید."),
            OperationCanceledException when http.RequestAborted.IsCancellationRequested =>
                (499, "cancelled", "درخواست لغو شد."),
            _ => (StatusCodes.Status500InternalServerError, "server_error", "خطای داخلی رخ داد. دوباره تلاش کنید."),
        };

        if (status >= 500)
        {
            logger.LogError(exception, "Unhandled exception for {Method} {Path}", http.Request.Method, http.Request.Path);
        }

        http.Response.StatusCode = status;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            Exception = exception,
            ProblemDetails =
            {
                Status = status,
                Title = message,
                Extensions = exception is Features.Patients.PossibleDuplicateException duplicate
                    ? new Dictionary<string, object?> { ["code"] = code, ["matches"] = duplicate.Matches }
                    : new Dictionary<string, object?> { ["code"] = code },
            },
        });
    }
}
