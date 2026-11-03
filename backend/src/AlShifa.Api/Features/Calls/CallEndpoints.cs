using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.RegularExpressions;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Clinical;
using AlShifa.Api.Features.Finance;
using AlShifa.Api.Features.Visits;
using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AlShifa.Api.Features.Calls;

public sealed record CreateCallRequest(CallDesk Desk, long? VisitId, long? TokenId, bool Recall);

public sealed record CallOutcomeRequest(CallOutcome Outcome);

public sealed record CallRow(long Id, int QueueNumber, string Destination, bool IsRecall, string Status, string? Outcome, string? FailureReason, DateTime RequestedAt, DateTime? FinishedAt);

public sealed record CallQueueEntry(long? VisitId, long? TokenId, int QueueNumber);

public sealed record CallQueueRow(long? VisitId, long? TokenId, int QueueNumber, CallRow? LastCall);

public sealed record CallDeskView(bool StationOnline, IReadOnlyList<CallQueueRow> Rows);

public sealed record ClaimStationRequest([Required, MaxLength(64)] string StationId, bool TakeOver);

public sealed record NextCallRequest([Required, MaxLength(64)] string StationId);

public sealed record CallResultRequest([Required, MaxLength(64)] string StationId, bool Played, [MaxLength(200)] string? Reason);

public sealed record NextCallDto(long Id, int QueueNumber, string Destination, IReadOnlyList<string> Clips);

public sealed record StationHistoryRow(long Id, string? VisitNumber, int QueueNumber, string Destination, bool IsRecall, string Status, string? Outcome, string? FailureReason, DateTime RequestedAt, DateTime? FinishedAt, string? RequestedBy);

public sealed record StationStatus(bool Active, bool OtherStationOnline, DateTime? LastSeenAt, int ClipsTotal, int ClipsMissing, IReadOnlyList<string> MissingSample, IReadOnlyList<string> TestClips, IReadOnlyList<StationHistoryRow> History);

public sealed record CallDeskRoom(CallDesk Desk, int? Room);

public sealed record CallDoctorRoom(long StaffId, int? Room);

public sealed record SaveCallSettingsRequest(IReadOnlyList<CallDeskRoom> Desks, IReadOnlyList<CallDoctorRoom> Doctors);

/// <summary>
/// Patient calling on top of the existing queues. Only a person pressing the button creates a call: moving a patient
/// between departments never does. Delivery is at most once: the station claims one row at a time and a claimed or
/// outdated row is never offered again, so a network outage cannot cause a repeat. Staff recall by hand instead.
/// </summary>
public static partial class CallEndpoints
{
    /// <summary>Longer than a background tab's throttled timer, so a station does not look gone while it is still working.</summary>
    public static readonly TimeSpan StationFreshFor = TimeSpan.FromSeconds(90);

    /// <summary>An older announcement would call people who have long since moved on.</summary>
    public static readonly TimeSpan QueuedExpiresAfter = TimeSpan.FromSeconds(120);

    public static readonly TimeSpan PlayingExpiresAfter = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan SeenWriteInterval = TimeSpan.FromSeconds(15);

    private const string StationKey = "calls.station";

    /// <summary>Who may call for each desk, and who hears that the desk's calls changed.</summary>
    private static readonly Dictionary<CallDesk, (string Permission, string Group)> Desks = new()
    {
        [CallDesk.Reception] = ("visitfees.create", Groups.Reception),
        [CallDesk.Vision] = ("preexam.edit", Groups.Triage),
        [CallDesk.Doctor] = ("consultation.edit", Groups.Doctor),
        [CallDesk.Refraction] = ("preexam.edit", Groups.Triage),
        [CallDesk.Imaging] = ("diagnostics.edit", Groups.Lab),
        [CallDesk.Lab] = ("diagnostics.edit", Groups.Lab),
        [CallDesk.Pharmacy] = ("pharmacy.create", Groups.Pharmacy),
        [CallDesk.Optical] = ("optical.create", Groups.Optical),
    };

    [GeneratedRegex("^[a-z0-9-]{1,40}\\.mp3$")]
    private static partial Regex ClipName();

    public static void MapCalls(this IEndpointRouteBuilder app)
    {
        var calls = app.MapGroup("/api/calls").WithTags("Calls");

        calls.MapGet("/desk/{desk}", async (CallDesk desk, AppDbContext db, CurrentUser user, HttpContext http, CancellationToken ct) =>
        {
            EnsureDesk(http, desk);
            var entries = await Eligible(db, desk, user.Id).Take(200).ToListAsync(ct);
            var visitIds = entries.Where(e => e.VisitId != null).Select(e => e.VisitId).ToList();
            var tokenIds = entries.Where(e => e.TokenId != null).Select(e => e.TokenId).ToList();
            var history = await db.PatientCalls.AsNoTracking()
                .Where(c => c.Desk == desk && ((c.VisitId != null && visitIds.Contains(c.VisitId)) || (c.VisitTokenId != null && tokenIds.Contains(c.VisitTokenId))))
                .OrderBy(c => c.Id)
                .ToListAsync(ct);

            var now = DateTime.UtcNow;
            var rows = entries.Select(e =>
            {
                var last = history.LastOrDefault(c => e.VisitId != null ? c.VisitId == e.VisitId : c.VisitTokenId == e.TokenId);
                return new CallQueueRow(e.VisitId, e.TokenId, e.QueueNumber, last is null ? null : ToRow(last, now));
            }).ToList();
            return Results.Ok(new CallDeskView(IsFresh(await ReadStationAsync(db, ct), now), rows));
        }).RequireAuthorization();

        // No Idempotency wrapper: the visit lock must be the transaction's first step so the checks below read what the previous holder committed.
        calls.MapPost("/", async (CreateCallRequest request, AppDbContext db, ClinicalAccess access, CurrentUser user, AuditService audit, Outbox outbox, IConfiguration config, IHostEnvironment env, HttpContext http, CancellationToken ct) =>
        {
            var desk = EnsureDesk(http, request.Desk);
            if ((request.Desk == CallDesk.Reception) != (request.TokenId is not null) || (request.VisitId is null) == (request.TokenId is null))
            {
                throw new AppException(StatusCodes.Status400BadRequest, "invalid_call", "مریض فراخوان مشخص نیست.");
            }

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            string? visitNumber = null;
            if (request.VisitId is { } visitId)
            {
                visitNumber = (await VisitService.LockAsync(db, visitId, ct)).VisitNumber;
                if (request.Desk == CallDesk.Doctor)
                {
                    await access.EnsureResponsibleAsync(visitId, ct);
                }
            }
            else
            {
                var tokenId = request.TokenId!.Value;
                _ = await db.VisitTokens.FromSqlInterpolated($"SELECT * FROM VisitTokens WHERE Id = {tokenId} FOR UPDATE").AsNoTracking().SingleOrDefaultAsync(ct) ?? throw AppException.NotFound();
            }

            var entry = await Eligible(db, request.Desk, user.Id, request.VisitId, request.TokenId).SingleOrDefaultAsync(ct)
                ?? throw new AppException(StatusCodes.Status409Conflict, "not_in_queue", "این مریض اکنون در صف این بخش نیست.");

            var now = DateTime.UtcNow;
            var previous = await db.PatientCalls.AsNoTracking()
                .Where(c => c.Desk == request.Desk && c.VisitId == request.VisitId && c.VisitTokenId == request.TokenId)
                .OrderByDescending(c => c.Id)
                .FirstOrDefaultAsync(ct);
            if (previous is not null && Effective(previous, now) is CallStatus.Queued or CallStatus.Playing)
            {
                throw new AppException(StatusCodes.Status409Conflict, "already_calling", "فراخوان این مریض هنوز در نوبت پخش است.");
            }

            if (request.Recall && previous is null)
            {
                throw new AppException(StatusCodes.Status409Conflict, "no_previous_call", "این مریض هنوز فراخوان نشده است.");
            }

            if (!IsFresh(await ReadStationAsync(db, ct), now))
            {
                throw new AppException(StatusCodes.Status409Conflict, "no_audio_station", "مرکز پخش صوت فعال نیست. فراخوان پخش نمی‌شود؛ رایانه اسپیکر را بررسی کنید.");
            }

            var destination = await DestinationAsync(db, request.Desk, user.Id, ct);
            var clips = VoicePack.Clips(entry.QueueNumber, destination, request.Recall);
            var root = VoicePack.Root(config, env);
            if (entry.QueueNumber is < 1 or > VoicePack.MaxNumber || clips.Any(c => !File.Exists(Path.Combine(root, c))))
            {
                throw new AppException(StatusCodes.Status409Conflict, "voice_clip_missing", $"فایل صوتی این اعلان موجود نیست ({string.Join("، ", clips.Where(c => !File.Exists(Path.Combine(root, c))))}). مجموعه صوتی را بررسی کنید.");
            }

            var call = new PatientCall
            {
                VisitId = request.VisitId,
                VisitTokenId = request.TokenId,
                Desk = request.Desk,
                QueueNumber = entry.QueueNumber,
                Destination = destination,
                IsRecall = request.Recall,
                RequestedByUserId = user.Id,
            };
            db.PatientCalls.Add(call);
            audit.Add(request.Recall ? "PatientRecalled" : "PatientCalled", request.VisitId is null ? nameof(VisitToken) : nameof(Visit), (request.VisitId ?? request.TokenId).ToString(),
                $"{visitNumber ?? "-"} | {request.Desk} | {destination} | {entry.QueueNumber}");
            outbox.Add("PatientCallRequested", new { desk = request.Desk.ToString() }, Groups.Audio);
            outbox.Add("PatientCallChanged", new { desk = request.Desk.ToString() }, desk.Group);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Created($"/api/calls/{call.Id}", ToRow(call, now));
        }).RequireAuthorization();

        // Recording who came changes nothing else: the patient's status stays with the department's own workflow.
        calls.MapPost("/{id:long}/outcome", async (long id, CallOutcomeRequest request, AppDbContext db, ClinicalAccess access, CurrentUser user, AuditService audit, Outbox outbox, HttpContext http, CancellationToken ct) =>
        {
            if (!Enum.IsDefined(request.Outcome))
            {
                throw new AppException(StatusCodes.Status400BadRequest, "invalid_outcome", "نتیجه فراخوان معتبر نیست.");
            }

            var call = await db.PatientCalls.SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw AppException.NotFound();
            var desk = EnsureDesk(http, call.Desk);
            if (call.Desk == CallDesk.Doctor && call.VisitId is { } visitId)
            {
                await access.EnsureResponsibleAsync(visitId, ct);
            }

            // The patient is here, so an announcement still waiting must not go out over the speaker.
            if (call.Status == CallStatus.Queued)
            {
                call.Status = CallStatus.Cancelled;
                call.FinishedAt = DateTime.UtcNow;
            }

            call.Outcome = request.Outcome;
            call.OutcomeByUserId = user.Id;
            call.OutcomeAt = DateTime.UtcNow;
            audit.Add("PatientCallOutcome", nameof(PatientCall), id.ToString(), request.Outcome.ToString());
            outbox.Add("PatientCallChanged", new { desk = call.Desk.ToString() }, desk.Group);
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToRow(call, DateTime.UtcNow));
        }).RequireAuthorization();

        calls.MapPost("/station/claim", async (ClaimStationRequest request, AppDbContext db, AuditService audit, CancellationToken ct) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var row = await LockStationAsync(db, ct);
            var current = Parse(row?.Value);
            var now = DateTime.UtcNow;
            if (current is not null && current.Value.Id != request.StationId && IsFresh(current, now) && !request.TakeOver)
            {
                throw new AppException(StatusCodes.Status409Conflict, "station_busy", "رایانه دیگری اکنون مرکز پخش صوت است. فقط یک مرکز می‌تواند فعال باشد.");
            }

            WriteStation(db, row, request.StationId, now);
            if (current?.Id != request.StationId)
            {
                audit.Add("AudioStationClaimed", "Setting", StationKey);
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        }).RequirePermission("calls.manage");

        // Also the station's heartbeat, so a station that cannot play stops being reported as online.
        calls.MapPost("/station/next", async (NextCallRequest request, AppDbContext db, Outbox outbox, CancellationToken ct) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var row = await LockStationAsync(db, ct);
            var current = Parse(row?.Value);
            if (current?.Id != request.StationId)
            {
                throw new AppException(StatusCodes.Status409Conflict, "station_replaced", "رایانه دیگری مرکز پخش صوت شده است. پخش در این رایانه متوقف شد.");
            }

            var now = DateTime.UtcNow;
            if (now - current.Value.SeenAt > SeenWriteInterval)
            {
                WriteStation(db, row, request.StationId, now);
            }

            var open = await db.PatientCalls.Where(c => c.Status == CallStatus.Queued || c.Status == CallStatus.Playing).OrderBy(c => c.Id).ToListAsync(ct);
            foreach (var stale in open.Where(c => Effective(c, now) == CallStatus.Failed))
            {
                stale.FailureReason = stale.Status == CallStatus.Playing ? "پخش کامل نشد." : "مرکز پخش به موقع در دسترس نبود.";
                stale.Status = CallStatus.Failed;
                stale.FinishedAt = now;
                outbox.Add("PatientCallChanged", new { desk = stale.Desk.ToString() }, Desks[stale.Desk].Group);
            }

            // One at a time even if a second tab asks, so announcements can never overlap.
            var next = open.Any(c => c.Status == CallStatus.Playing) ? null : open.FirstOrDefault(c => c.Status == CallStatus.Queued);
            if (next is not null)
            {
                next.Status = CallStatus.Playing;
                next.ClaimedAt = now;
                outbox.Add("PatientCallChanged", new { desk = next.Desk.ToString() }, Desks[next.Desk].Group);
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return next is null
                ? Results.NoContent()
                : Results.Ok(new NextCallDto(next.Id, next.QueueNumber, VoicePack.Label(next.Destination), VoicePack.Clips(next.QueueNumber, next.Destination, next.IsRecall)));
        }).RequirePermission("calls.manage");

        calls.MapPost("/{id:long}/result", async (long id, CallResultRequest request, AppDbContext db, Outbox outbox, CancellationToken ct) =>
        {
            if ((await ReadStationAsync(db, ct))?.Id != request.StationId)
            {
                throw new AppException(StatusCodes.Status409Conflict, "station_replaced", "رایانه دیگری مرکز پخش صوت شده است. پخش در این رایانه متوقف شد.");
            }

            var call = await db.PatientCalls.SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw AppException.NotFound();
            if (call.Status != CallStatus.Playing)
            {
                return Results.NoContent();
            }

            call.Status = request.Played ? CallStatus.Played : CallStatus.Failed;
            call.FailureReason = request.Played ? null : Text.Clean(request.Reason) ?? "پخش نشد.";
            call.FinishedAt = DateTime.UtcNow;
            outbox.Add("PatientCallChanged", new { desk = call.Desk.ToString() }, Desks[call.Desk].Group);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequirePermission("calls.manage");

        // Numbers and destinations only. This page runs on a shared computer with a role that opens no patient data.
        calls.MapGet("/station/status", async (string stationId, AppDbContext db, IConfiguration config, IHostEnvironment env, CancellationToken ct) =>
        {
            var station = await ReadStationAsync(db, ct);
            var now = DateTime.UtcNow;
            var today = KabulClock.Today;
            var from = today.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddHours(-4.5);
            var history = await db.PatientCalls.AsNoTracking()
                .Where(c => c.RequestedAt >= from)
                .OrderByDescending(c => c.Id)
                .Take(15)
                .Select(c => new { Call = c, VisitNumber = c.Visit != null ? c.Visit.VisitNumber : null, RequestedBy = db.Users.Where(u => u.Id == c.RequestedByUserId).Select(u => u.FullName).FirstOrDefault() })
                .ToListAsync(ct);

            var root = VoicePack.Root(config, env);
            var present = Directory.Exists(root) ? Directory.EnumerateFiles(root, "*.mp3").Select(f => Path.GetFileName(f)).ToHashSet() : [];
            var required = VoicePack.AllClips().ToList();
            var missing = required.Where(c => !present.Contains(c)).ToList();

            return Results.Ok(new StationStatus(
                station?.Id == stationId && IsFresh(station, now),
                station is not null && station.Value.Id != stationId && IsFresh(station, now),
                station?.SeenAt,
                required.Count,
                missing.Count,
                missing.Take(5).ToList(),
                VoicePack.Clips(100, VoicePack.Sections[CallDesk.Vision].Clip, false),
                history.Select(h => new StationHistoryRow(h.Call.Id, h.VisitNumber, h.Call.QueueNumber, VoicePack.Label(h.Call.Destination), h.Call.IsRecall, Effective(h.Call, now).ToString(), h.Call.Outcome?.ToString(), h.Call.FailureReason, h.Call.RequestedAt, h.Call.FinishedAt, h.RequestedBy)).ToList()));
        }).RequirePermission("calls.manage");

        calls.MapGet("/voice/{name}", (string name, IConfiguration config, IHostEnvironment env) =>
        {
            // The pattern allows no separator, so a request cannot leave the voice folder.
            var path = Path.Combine(VoicePack.Root(config, env), name);
            if (!ClipName().IsMatch(name) || !File.Exists(path))
            {
                throw new AppException(StatusCodes.Status404NotFound, "voice_clip_missing", $"فایل صوتی {name} موجود نیست.");
            }

            return Results.File(path, "audio/mpeg");
        }).RequirePermission("calls.manage");

        calls.MapGet("/settings", async (AppDbContext db, CancellationToken ct) =>
        {
            var values = await db.Settings.AsNoTracking().Where(s => s.Key.StartsWith("calls.room.")).ToDictionaryAsync(s => s.Key, s => s.Value, ct);
            var doctors = await db.Staff.AsNoTracking().Where(s => s.IsDoctor && s.IsActive).OrderBy(s => s.FullName).Select(s => new { s.Id, s.FullName, HasAccount = s.UserId != null }).ToListAsync(ct);
            return Results.Ok(new
            {
                MaxRoom = VoicePack.MaxRoom,
                Desks = VoicePack.Sections.Select(s => new { Desk = s.Key.ToString(), SectionLabel = s.Value.Label, Room = ParseRoom(values.GetValueOrDefault(DeskRoomKey(s.Key))) }),
                Doctors = doctors.Select(d => new { StaffId = d.Id, Name = d.FullName, d.HasAccount, Room = ParseRoom(values.GetValueOrDefault(DoctorRoomKey(d.Id))) }),
            });
        }).RequirePermission("settings.view");

        calls.MapPut("/settings", async (SaveCallSettingsRequest request, AppDbContext db, AuditService audit, CancellationToken ct) =>
        {
            if (request.Desks.Select(d => d.Room).Concat(request.Doctors.Select(d => d.Room)).Any(r => r is < 1 or > VoicePack.MaxRoom))
            {
                throw new AppException(StatusCodes.Status400BadRequest, "invalid_room", $"شماره اتاق باید بین ۱ و {VoicePack.MaxRoom} باشد؛ مجموعه صوتی فقط همین اتاق‌ها را دارد.");
            }

            if (request.Desks.Any(d => !VoicePack.Sections.ContainsKey(d.Desk)))
            {
                throw new AppException(StatusCodes.Status400BadRequest, "invalid_desk", "بخش انتخاب‌شده معتبر نیست.");
            }

            var doctorIds = request.Doctors.Select(d => d.StaffId).ToList();
            if (await db.Staff.CountAsync(s => doctorIds.Contains(s.Id) && s.IsDoctor, ct) != doctorIds.Distinct().Count())
            {
                throw new AppException(StatusCodes.Status400BadRequest, "unknown_doctor", "داکتر انتخاب‌شده معتبر نیست.");
            }

            foreach (var desk in request.Desks)
            {
                await Settings.SettingsEndpoints.UpsertAsync(db, DeskRoomKey(desk.Desk), desk.Room?.ToString(CultureInfo.InvariantCulture), ct);
            }

            foreach (var doctor in request.Doctors)
            {
                await Settings.SettingsEndpoints.UpsertAsync(db, DoctorRoomKey(doctor.StaffId), doctor.Room?.ToString(CultureInfo.InvariantCulture), ct);
            }

            audit.Add("CallSettingsChanged", "Setting", null);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequirePermission("settings.edit");
    }

    /// <summary>
    /// Who stands in a desk's queue right now. The same rule fills the desk's call buttons and guards the call itself.
    /// A queue number is only meaningful on its own day, so nothing from earlier days can be called.
    /// </summary>
    private static IQueryable<CallQueueEntry> Eligible(AppDbContext db, CallDesk desk, long userId, long? visitId = null, long? tokenId = null)
    {
        var today = KabulClock.Today;
        if (desk == CallDesk.Reception)
        {
            var tokens = db.VisitTokens.AsNoTracking().Where(t => t.TokenDate == today && t.VisitId == null && t.Invoice!.Status != InvoiceStatus.Void && (t.Invoice.Paid == 0 || t.Invoice.Paid > t.Invoice.Refunded));
            if (tokenId is { } id)
            {
                tokens = tokens.Where(t => t.Id == id);
            }

            return tokens.OrderBy(t => t.QueueNumber).Select(t => new CallQueueEntry(null, t.Id, t.QueueNumber));
        }

        var visits = db.Visits.AsNoTracking().Where(v => v.VisitDate == today && v.Status != VisitStatus.Cancelled);
        if (visitId is { } oneVisit)
        {
            visits = visits.Where(v => v.Id == oneVisit);
        }

        var openOrders = db.DiagnosticOrders.Where(o => o.Status == DiagnosticStatus.Requested || o.Status == DiagnosticStatus.InProgress);
        var paidOrders = openOrders.Where(o => o.Invoice != null && o.Invoice.Status != InvoiceStatus.Void && o.Invoice.Total - o.Invoice.Discount <= o.Invoice.Paid - o.Invoice.Refunded);
        var awaited = DiagnosticGate.Awaited(db);

        var queue = desk switch
        {
            CallDesk.Vision => ClinicalEndpoints.VisionQueue(visits),

            // While the doctor is waiting on a request, the patient is at that department and comes back to this queue when it finishes.
            CallDesk.Doctor => ClinicalEndpoints.DoctorQueue(db, visits, userId)
                .Where(v => (v.Status == VisitStatus.ReadyForDoctor || v.Status == VisitStatus.InConsultation) && !awaited.Any(o => o.VisitId == v.Id)),

            CallDesk.Refraction => visits.Where(v => paidOrders.Any(o => o.VisitId == v.Id && o.Test!.Kind == DiagnosticKind.Refraction)),
            CallDesk.Imaging => visits.Where(v => paidOrders.Any(o => o.VisitId == v.Id && o.Test!.Kind == DiagnosticKind.Imaging)),

            // A lab test may have no fee at all; one that has a fee waits for it like any other paid request.
            CallDesk.Lab => visits.Where(v => openOrders.Any(o => o.VisitId == v.Id && o.Test!.Kind == DiagnosticKind.Lab && o.InvoiceId == null)
                || paidOrders.Any(o => o.VisitId == v.Id && o.Test!.Kind == DiagnosticKind.Lab)),

            CallDesk.Pharmacy => visits.Where(v => db.MedicalPrescriptions.Any(p => p.VisitId == v.Id && (p.Status == PrescriptionStatus.Issued || p.Status == PrescriptionStatus.PartiallyDispensed))),
            CallDesk.Optical => visits.Where(v => db.OpticalPrescriptions.Any(p => p.VisitId == v.Id && !p.IsCancelled
                && !db.OpticalOrders.Any(o => o.OpticalPrescriptionId == p.Id && o.Status == Inventory.OpticalOrderStatus.Delivered))),
            _ => throw new AppException(StatusCodes.Status400BadRequest, "invalid_desk", "بخش انتخاب‌شده معتبر نیست."),
        };

        return queue.OrderBy(v => v.QueueNumber).Select(v => new CallQueueEntry(v.Id, null, v.QueueNumber));
    }

    private static async Task<string> DestinationAsync(AppDbContext db, CallDesk desk, long userId, CancellationToken ct)
    {
        if (desk == CallDesk.Doctor)
        {
            var staffId = await db.Staff.AsNoTracking().Where(s => s.UserId == userId && s.IsDoctor && s.IsActive).Select(s => (long?)s.Id).FirstOrDefaultAsync(ct);
            var doctorRoom = staffId is { } id ? ParseRoom(await RoomValueAsync(db, DoctorRoomKey(id), ct)) : null;
            return doctorRoom is { } value
                ? VoicePack.Room(value)
                : throw new AppException(StatusCodes.Status409Conflict, "room_not_set", "برای حساب شما اتاق داکتر تعیین نشده است. در تنظیمات، بخش فراخوان را تکمیل کنید.");
        }

        var room = ParseRoom(await RoomValueAsync(db, DeskRoomKey(desk), ct));
        return room is { } number ? VoicePack.Room(number) : VoicePack.Sections[desk].Clip;
    }

    private static Task<string?> RoomValueAsync(AppDbContext db, string key, CancellationToken ct) =>
        db.Settings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync(ct);

    private static string DeskRoomKey(CallDesk desk) => $"calls.room.{desk}";

    private static string DoctorRoomKey(long staffId) => $"calls.room.staff.{staffId}";

    private static int? ParseRoom(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var room) && room is >= 1 and <= VoicePack.MaxRoom ? room : null;

    private static (string Permission, string Group) EnsureDesk(HttpContext http, CallDesk desk)
    {
        if (!Desks.TryGetValue(desk, out var rule))
        {
            throw new AppException(StatusCodes.Status400BadRequest, "invalid_desk", "بخش انتخاب‌شده معتبر نیست.");
        }

        if (!http.User.HasClaim(RolePermissions.ClaimType, rule.Permission))
        {
            throw new AppException(StatusCodes.Status403Forbidden, "forbidden", "شما اجازه فراخوان در این بخش را ندارید.");
        }

        return rule;
    }

    /// <summary>The stored status can lag when the station is gone, so readers apply the same deadlines the station's poll will.</summary>
    private static CallStatus Effective(PatientCall call, DateTime now) => call.Status switch
    {
        CallStatus.Queued when now - call.RequestedAt > QueuedExpiresAfter => CallStatus.Failed,
        CallStatus.Playing when call.ClaimedAt is null || now - call.ClaimedAt > PlayingExpiresAfter => CallStatus.Failed,
        _ => call.Status,
    };

    private static CallRow ToRow(PatientCall c, DateTime now)
    {
        var status = Effective(c, now);
        return new CallRow(c.Id, c.QueueNumber, VoicePack.Label(c.Destination), c.IsRecall, status.ToString(), c.Outcome?.ToString(),
            status == CallStatus.Failed ? c.FailureReason ?? "پخش نشد." : null, c.RequestedAt, c.FinishedAt);
    }

    private static bool IsFresh((string Id, DateTime SeenAt)? station, DateTime now) =>
        station is not null && now - station.Value.SeenAt <= StationFreshFor;

    private static (string Id, DateTime SeenAt)? Parse(string? value)
    {
        var parts = value?.Split('|');
        return parts is { Length: 2 } && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            ? (parts[0], new DateTime(ticks, DateTimeKind.Utc))
            : null;
    }

    private static async Task<(string Id, DateTime SeenAt)?> ReadStationAsync(AppDbContext db, CancellationToken ct) =>
        Parse(await db.Settings.AsNoTracking().Where(s => s.Key == StationKey).Select(s => s.Value).FirstOrDefaultAsync(ct));

    /// <summary>Serialises everything the station does, so two computers can never both be handed calls.</summary>
    private static Task<Setting?> LockStationAsync(AppDbContext db, CancellationToken ct) =>
        db.Settings.FromSqlInterpolated($"SELECT * FROM Settings WHERE `Key` = {StationKey} FOR UPDATE").SingleOrDefaultAsync(ct);

    private static void WriteStation(AppDbContext db, Setting? row, string stationId, DateTime now)
    {
        var value = $"{stationId}|{now.Ticks.ToString(CultureInfo.InvariantCulture)}";
        if (row is null)
        {
            db.Settings.Add(new Setting { Key = StationKey, Value = value });
        }
        else
        {
            row.Value = value;
        }
    }
}
