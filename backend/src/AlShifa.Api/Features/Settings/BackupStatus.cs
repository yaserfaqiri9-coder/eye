using System.Text.Json;

namespace AlShifa.Api.Features.Settings;

public sealed record BackupStatusDto(bool Configured, DateTime? LastSuccessAt, string? LastError, bool NeedsAttention);

/// <summary>Reads the status file written by deploy/windows/backup.ps1, so a backup that stops working cannot go unnoticed.</summary>
public static class BackupStatus
{
    private sealed record StatusFile(DateTime? LastSuccessAt, string? LastError);

    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    public static BackupStatusDto Read(string? path, DateTime nowUtc)
    {
        if (string.IsNullOrEmpty(path))
        {
            return new BackupStatusDto(false, null, null, false);
        }

        StatusFile? status;
        try
        {
            status = JsonSerializer.Deserialize<StatusFile>(File.ReadAllText(path), JsonSerializerOptions.Web);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // No readable file means no backup can be confirmed, which is the case this warning exists for.
            return new BackupStatusDto(true, null, "وضعیت پشتیبان خوانده نشد.", true);
        }

        var last = status?.LastSuccessAt?.ToUniversalTime();
        var stale = last is null || nowUtc - last > MaxAge;
        return new BackupStatusDto(true, last, status?.LastError, stale || !string.IsNullOrEmpty(status?.LastError));
    }
}
