using System.Diagnostics;
using System.Text.Json;

namespace AlShifa.Api.Features.Settings;

public sealed record BackupFileDto(string Name, long SizeBytes, DateTime CreatedAt);

public sealed record BackupStatusDto(
    bool Configured,
    DateTime? LastSuccessAt,
    DateTime? LastRunAt,
    DateTime? RunningSince,
    string? LastError,
    bool NeedsAttention,
    string? Destination,
    string? OptionalDestination,
    long? LastSizeBytes,
    int? LastDurationSeconds,
    IReadOnlyList<BackupFileDto> Backups);

/// <summary>Reads the status file written by deploy/windows/backup.ps1, so a backup that stops working cannot go unnoticed.</summary>
public static class BackupStatus
{
    private sealed record StatusFile(
        DateTime? LastRunAt,
        DateTime? LastSuccessAt,
        string? LastError,
        DateTime? RunningSince,
        string? Destination,
        string? OptionalDestination,
        long? LastSizeBytes,
        int? LastDurationSeconds,
        List<BackupFileDto>? Backups);

    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    // A run that was cut off by a power failure never clears its start time; after this long it is no longer "running".
    private static readonly TimeSpan MaxRunTime = TimeSpan.FromHours(2);

    public static BackupStatusDto Read(string? path, DateTime nowUtc)
    {
        if (string.IsNullOrEmpty(path))
        {
            return new BackupStatusDto(false, null, null, null, null, false, null, null, null, null, []);
        }

        StatusFile? status;
        try
        {
            status = JsonSerializer.Deserialize<StatusFile>(File.ReadAllText(path), JsonSerializerOptions.Web);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // No readable file means no backup can be confirmed, which is the case this warning exists for.
            return new BackupStatusDto(true, null, null, null, "وضعیت پشتیبان خوانده نشد.", true, null, null, null, null, []);
        }

        var last = status?.LastSuccessAt?.ToUniversalTime();
        var stale = last is null || nowUtc - last > MaxAge;
        var running = status?.RunningSince?.ToUniversalTime() is { } since && nowUtc - since < MaxRunTime ? since : (DateTime?)null;
        return new BackupStatusDto(
            true,
            last,
            status?.LastRunAt?.ToUniversalTime(),
            running,
            status?.LastError,
            stale || !string.IsNullOrEmpty(status?.LastError),
            status?.Destination,
            status?.OptionalDestination,
            status?.LastSizeBytes,
            status?.LastDurationSeconds,
            status?.Backups?.OrderByDescending(b => b.CreatedAt).ToList() ?? []);
    }
}

/// <summary>Starts the scheduled backup task now, instead of waiting for its next run.</summary>
public interface IBackupTrigger
{
    void Request();
}

/// <summary>
/// The backup runs as SYSTEM and the application deliberately cannot start it. Instead the application writes this event,
/// and the "AlShifa Backup" task has a trigger on it (see Add-BackupRequestTrigger in deploy/windows/common.ps1).
/// Anyone able to write the event can only cause an extra backup, and the task ignores a request while one is running.
/// </summary>
public sealed class EventLogBackupTrigger : IBackupTrigger
{
    public const string Source = "AlShifa.Api";
    public const int EventId = 4100;

    // The task's trigger matches this text exactly, so the application's ordinary log messages never start a backup.
    public const string Message = "AlShifa backup request";

    public void Request()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        EventLog.WriteEntry(Source, Message, EventLogEntryType.Information, EventId);
    }
}
