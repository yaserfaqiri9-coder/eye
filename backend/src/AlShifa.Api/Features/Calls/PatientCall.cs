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

    /// <summary>No station was online, so the desk that pressed the button plays it. The station never takes these.</summary>
    public bool LocalPlayback { get; set; }

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

/// <summary>Versioned recordings for new playback; legacy names remain for compatibility with older installation tools.</summary>
public static class VoicePack
{
    // Content-addressed directory: new recordings cannot reuse the old browser cache.
    public const string RecordedVersion = "recorded-c98dd13ed170";
    public const int RecordedMaxNumber = 50;
    public const string RecordedBase = "/audio/" + RecordedVersion + "/WAV/";
    public static readonly string[] RecordedDestinations = ["doctor", "refraction", "imaging", "pharmacy", "optical", "initial_exam", "reception", "cashier", "surgery"];

    /// <summary>Use stored machine identifiers, including the original desk for numbered rooms. Never infer a destination from display text.</summary>
    public static string? RecordedDestination(string destination, CallDesk? desk = null)
    {
        if (destination.StartsWith("room-", StringComparison.Ordinal))
        {
            return desk switch
            {
                CallDesk.Doctor => "doctor", CallDesk.Vision => "initial_exam", CallDesk.Reception => "reception",
                CallDesk.Refraction => "refraction", CallDesk.Imaging => "imaging", CallDesk.Pharmacy => "pharmacy",
                CallDesk.Optical => "optical", _ => null,
            };
        }
        return destination == "vision" ? "initial_exam" : RecordedDestinations.Contains(destination) ? destination : null;
    }

    public static string[] RecordedClips(int number, string destination, CallDesk? desk = null) =>
        number is >= 1 and <= RecordedMaxNumber && RecordedDestination(destination, desk) is { } key
            ? [$"{RecordedBase}numbers/number_{number:D3}.wav", $"{RecordedBase}destinations/destination_{key}.wav"] : [];

    public static IEnumerable<string> RecordedFiles() =>
        Enumerable.Range(1, RecordedMaxNumber).Select(n => $"numbers/number_{n:D3}.wav")
            .Concat(RecordedDestinations.Select(d => $"destinations/destination_{d}.wav"));

    public static string RecordedRoot(IHostEnvironment env)
    {
        var published = Path.Combine(env.ContentRootPath, "wwwroot", "audio", RecordedVersion, "WAV");
        // A development checkout serves its static files with Vite; published builds serve wwwroot.
        return Directory.Exists(published) ? published : Path.GetFullPath(Path.Combine(env.ContentRootPath, "..", "..", "..", "frontend", "public", "audio", RecordedVersion, "WAV"));
    }

    // These limits and legacy MP3 helpers are used by older voice installation tools, not recorded playback.
    public const int MaxNumber = 100;
    public const int MaxRoom = 20;

    /// <summary>Internal routing keys and labels. Doctor routing uses its assigned room; the recording says only "doctor".</summary>
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
            return $"اتاق شماره {destination["room-".Length..].TrimStart('0')}";
        }

        return Sections.Values.FirstOrDefault(s => s.Clip == destination).Label ?? destination;
    }

    public static string Root(IConfiguration config, IHostEnvironment env) =>
        Path.Combine(SettingsEndpoints.StorageRoot(config, env), "voice");
}
