using System.Text.Json;
using AlShifa.Api.Features.Auth;

namespace AlShifa.Api.Infrastructure;

public sealed record BackupStatusDto(string State, DateTime? LastSuccessUtc, DateTime? LastAttemptUtc, string? LastError);

/// <summary>
/// Reads the file the scheduled backup writes after every run. A task that stopped running reports nothing,
/// so age counts as a failure too; otherwise the warning would never appear in the case that matters most.
/// </summary>
public static class BackupStatusReader
{
    public const string Ok = "ok";
    public const string Failed = "failed";
    public const string Stale = "stale";
    public const string Never = "never";
    public const string NotConfigured = "not_configured";

    private sealed record StatusFile(DateTime? LastSuccessUtc, DateTime? LastAttemptUtc, string? LastError);

    public static BackupStatusDto Read(string path, TimeSpan maxAge, DateTime nowUtc)
    {
        StatusFile? file = null;
        try
        {
            file = JsonSerializer.Deserialize<StatusFile>(File.ReadAllText(path), JsonSerializerOptions.Web);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // Missing or half-written: treated the same as no backup having been recorded.
        }

        if (file?.LastSuccessUtc is not { } success)
        {
            return new BackupStatusDto(file?.LastError is null ? Never : Failed, null, file?.LastAttemptUtc, file?.LastError);
        }

        var state = file.LastError is not null ? Failed : nowUtc - success.ToUniversalTime() > maxAge ? Stale : Ok;
        return new BackupStatusDto(state, success, file.LastAttemptUtc, file.LastError);
    }
}

public static class SystemStatusEndpoints
{
    public static void MapSystemStatus(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/system/backup-status", (IConfiguration config) =>
        {
            var path = config["Backup:StatusFile"];
            return string.IsNullOrEmpty(path)
                ? Results.Ok(new BackupStatusDto(BackupStatusReader.NotConfigured, null, null, null))
                : Results.Ok(BackupStatusReader.Read(path, TimeSpan.FromHours(config.GetValue("Backup:MaxAgeHours", 14)), DateTime.UtcNow));
        }).RequirePermission("settings.view").WithTags("System");
    }
}
