using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace AlShifa.Api.Infrastructure;

public static class KabulClock
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kabul");

    public static DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Zone));
}

/// <summary>Signal group names. Membership is decided on the server from the user's permissions.</summary>
public static class Groups
{
    public const string Reception = "dept:reception";
    public const string Triage = "dept:triage";
    public const string Doctor = "dept:doctor";
    public const string Lab = "dept:lab";
    public const string Surgery = "dept:surgery";
    public const string Pharmacy = "dept:pharmacy";
    public const string Optical = "dept:optical";
    public const string Cashier = "dept:cashier";
    public const string Finance = "dept:finance";
    public const string Inventory = "dept:inventory";
    public const string Management = "dept:management";
    public const string Audio = "dept:audio";
}

/// <summary>Queues an event in the same transaction as the change, so it is sent only if the change commits.</summary>
public sealed class Outbox(AppDbContext db)
{
    public void Add(string type, object ids, params string[] groups) =>
        db.OutboxEvents.Add(new OutboxEvent
        {
            Type = type,
            Payload = JsonSerializer.Serialize(ids, JsonSerializerOptions.Web),
            Groups = string.Join(',', groups),
        });
}

/// <summary>
/// Runs a create operation at most once per Idempotency-Key. The unique index on (UserId, Key) makes a
/// concurrent duplicate wait for the first request and then replay its stored response.
/// </summary>
public sealed class Idempotency(AppDbContext db, CurrentUser user, IHttpContextAccessor accessor)
{
    private const int MySqlDuplicateEntry = 1062;

    public async Task<IResult> RunAsync<T>(object request, Func<Task<(int Status, T Body)>> action, CancellationToken ct = default)
    {
        var key = accessor.HttpContext?.Request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrWhiteSpace(key) || key.Length > 64)
        {
            throw new AppException(StatusCodes.Status400BadRequest, "idempotency_key_required", "درخواست شناسه تکرار معتبر ندارد.");
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, JsonSerializerOptions.Web))));
        var userId = user.Id;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var record = new IdempotencyRecord { UserId = userId, Key = key, RequestHash = hash };
        db.IdempotencyRecords.Add(record);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (IsDuplicate(e))
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            var stored = await db.IdempotencyRecords.AsNoTracking().SingleAsync(r => r.UserId == userId && r.Key == key, ct);
            if (stored.RequestHash != hash)
            {
                throw new AppException(StatusCodes.Status409Conflict, "idempotency_key_reused", "این شناسه برای درخواست دیگری استفاده شده است.");
            }

            return Results.Text(stored.ResponseBody, "application/json", statusCode: stored.StatusCode);
        }

        var (status, body) = await action();
        record.StatusCode = status;
        record.ResponseBody = JsonSerializer.Serialize(body, JsonSerializerOptions.Web);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Text(record.ResponseBody, "application/json", statusCode: status);
    }

    private static bool IsDuplicate(DbUpdateException e) =>
        e.InnerException is MySql.Data.MySqlClient.MySqlException { Number: MySqlDuplicateEntry };
}
