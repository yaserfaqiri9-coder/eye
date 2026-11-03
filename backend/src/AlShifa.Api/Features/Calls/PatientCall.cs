using System.ComponentModel.DataAnnotations;
using AlShifa.Api.Features.Settings;
using AlShifa.Api.Features.Visits;

namespace AlShifa.Api.Features.Calls;

public enum CallDesk
{
    Reception,
    Vision,
    Doctor,
    Refraction,
    Imaging,
    Lab,
    Pharmacy,
    Optical,
}

public enum CallStatus
{
    Queued,
    Playing,
    Played,
    Failed,
    Cancelled,
}

public enum CallOutcome
{
    Attended,
    NoShow,
}

/// <summary>
/// One announcement request and the call history at once. A row is played at most once, so a reconnecting station can
/// never repeat it. It holds no clinical or financial state and changes none.
/// </summary>
public class PatientCall
{
    public long Id { get; set; }

    public long? VisitId { get; set; }

    public Visit? Visit { get; set; }

    /// <summary>Reception calls a token that has no visit yet.</summary>
    public long? VisitTokenId { get; set; }

    public VisitToken? VisitToken { get; set; }

    public CallDesk Desk { get; set; }

    public int QueueNumber { get; set; }

    /// <summary>A section name or room-NN, resolved from settings when the call was made.</summary>
    [MaxLength(30)]
    public string Destination { get; set; } = "";

    public bool IsRecall { get; set; }

    public CallStatus Status { get; set; } = CallStatus.Queued;

    public CallOutcome? Outcome { get; set; }

    [MaxLength(200)]
    public string? FailureReason { get; set; }

    public long RequestedByUserId { get; set; }

    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;

    public DateTime? ClaimedAt { get; set; }

    public DateTime? FinishedAt { get; set; }

    public long? OutcomeByUserId { get; set; }

    public DateTime? OutcomeAt { get; set; }

    public int Version { get; set; }
}

/// <summary>The clip names of the local voice pack. The spoken text belongs to the pack tool, deploy/windows/voice-phrases.json.</summary>
public static class VoicePack
{
    public const int MaxNumber = 999;
    public const int MaxRoom = 20;

    /// <summary>The doctor's desk has no section: a doctor is always announced by room.</summary>
    public static readonly IReadOnlyDictionary<CallDesk, (string Clip, string Label)> Sections = new Dictionary<CallDesk, (string, string)>
    {
        [CallDesk.Reception] = ("reception", "بخش پذیرش"),
        [CallDesk.Vision] = ("vision", "بخش ویژن"),
        [CallDesk.Refraction] = ("refraction", "بخش رفرکشن"),
        [CallDesk.Imaging] = ("imaging", "بخش عکس‌برداری چشم"),
        [CallDesk.Lab] = ("lab", "بخش آزمایشگاه"),
        [CallDesk.Pharmacy] = ("pharmacy", "بخش دواخانه"),
        [CallDesk.Optical] = ("optical", "بخش عینک"),
    };

    public static string Room(int room) => $"room-{room:D2}";

    public static string[] Clips(int number, string destination, bool recall) =>
        [$"number-{number:D4}.mp3", $"{(recall ? "again" : "to")}-{destination}.mp3"];

    public static IEnumerable<string> AllClips()
    {
        var destinations = Sections.Values.Select(s => s.Clip).Concat(Enumerable.Range(1, MaxRoom).Select(Room)).ToList();
        return Enumerable.Range(1, MaxNumber).Select(n => $"number-{n:D4}.mp3")
            .Concat(destinations.SelectMany(d => new[] { $"to-{d}.mp3", $"again-{d}.mp3" }));
    }

    public static string Label(string destination)
    {
        if (destination.StartsWith("room-", StringComparison.Ordinal))
        {
            var digits = string.Concat(destination["room-".Length..].TrimStart('0').Select(c => (char)('۰' + (c - '0'))));
            return $"اتاق شماره {digits}";
        }

        return Sections.Values.FirstOrDefault(s => s.Clip == destination).Label ?? destination;
    }

    public static string Root(IConfiguration config, IHostEnvironment env) =>
        Path.Combine(SettingsEndpoints.StorageRoot(config, env), "voice");
}
