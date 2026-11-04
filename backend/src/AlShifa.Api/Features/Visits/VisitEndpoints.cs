using System.ComponentModel.DataAnnotations;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Clinical;
using AlShifa.Api.Features.Finance;
using AlShifa.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace AlShifa.Api.Features.Visits;

/// <param name="TokenId">The paid token this visit uses. Only for tokens issued before the patient-first flow.</param>
public sealed record CreateVisitRequest(long PatientId, int DepartmentId, long? DoctorStaffId, [MaxLength(300)] string? Reason, int? ServiceId = null, long? TokenId = null);

public sealed record VisitRow(
    long Id, string VisitNumber, int? QueueNumber, long PatientId, string PatientFileNumber, string PatientName,
    int DepartmentId, string DepartmentName, string? DoctorName, string Status, string? Reason, DateOnly VisitDate,
    DateTime CreatedAt, int Version, IReadOnlyList<string> NextStatuses, string PatientGender, long? DoctorStaffId = null, string FeeStatus = "None");

public sealed record ChangeVisitStatusRequest(VisitStatus Status, int Version);

public sealed record TransferDoctorRequest(long DoctorStaffId, [Required, MinLength(3), MaxLength(300)] string Reason, int Version);

public sealed record CreateAppointmentRequest(long PatientId, long DoctorStaffId, DateTime ScheduledAt, [MaxLength(300)] string? Note);

public sealed record AppointmentRow(long Id, long PatientId, string PatientFileNumber, string PatientName, long DoctorStaffId, string DoctorName, DateTime ScheduledAt, string Status, string? Note, long? VisitId, int Version);

public sealed record LookupItem(long Id, string Name);

public sealed class VisitService(AppDbContext db, DbContextOptions<AppDbContext> options, NumberService numbers, CurrentUser user, AuditService audit, Outbox outbox, InvoiceService invoices, VisitFeeService visitFees)
{
    public static readonly string[] EventGroups = [Groups.Reception, Groups.Triage, Groups.Doctor, Groups.Management];

    /// <summary>One sequence per day across all departments: a number announced over the speaker must mean exactly one patient.</summary>
    public const string QueueNumberScope = "queue";

    /// <summary>
    /// Serialises everything that decides where a patient goes next. Take it first in the transaction, before any plain
    /// read, so those reads see what the previous holder committed. Lock order everywhere: visit, order, invoice.
    /// </summary>
    public static async Task<Visit> LockAsync(AppDbContext db, long id, CancellationToken ct) =>
        await db.Visits.FromSqlInterpolated($"SELECT * FROM Visits WHERE Id = {id} FOR UPDATE").SingleOrDefaultAsync(ct)
        ?? throw AppException.NotFound();

    /// <summary>
    /// The desk's flow: a visit for a chosen patient, with its fee invoiced and nothing else. It holds no queue number and
    /// stands in no queue until the fee is settled. Must run inside a transaction.
    /// </summary>
    public async Task<Visit> AdmitAsync(CreateAdmissionRequest request, CancellationToken ct)
    {
        await LockPatientAndValidateAsync(request.PatientId, request.DepartmentId, request.DoctorStaffId, ct);
        await EnsureNoActiveVisitAsync(request.PatientId, ct);

        var visit = new Visit
        {
            PatientId = request.PatientId,
            DepartmentId = request.DepartmentId,
            DoctorStaffId = request.DoctorStaffId,
            Reason = Text.Clean(request.Reason),
            Status = VisitStatus.AwaitingPayment,
            VisitDate = KabulClock.Today,
            VisitNumber = $"V-{await numbers.NextAsync("visit", ct: ct):D7}",
            CreatedByUserId = user.Id,
        };
        db.Visits.Add(visit);
        await db.SaveChangesAsync(ct);

        await visitFees.CreateTokenAsync(visit.DepartmentId, visit.DoctorStaffId, visit.PatientId, ct, visit.Id);
        db.VisitStatusHistory.Add(new VisitStatusHistory { VisitId = visit.Id, ToStatus = visit.Status, ChangedByUserId = user.Id });
        audit.Add("VisitCreated", "Visit", visit.Id.ToString());
        outbox.Add("VisitCreated", new { visitId = visit.Id }, EventGroups);
        await db.SaveChangesAsync(ct);
        await ChargeExtraServiceAsync(visit, request.ServiceId, ct);
        return visit;
    }

    /// <summary>The patient row lock stops two desks opening two visits, and two fees, for one person.</summary>
    private async Task LockPatientAndValidateAsync(long patientId, int departmentId, long? doctorStaffId, CancellationToken ct)
    {
        var patients = await db.Patients.FromSqlInterpolated($"SELECT * FROM Patients WHERE Id = {patientId} FOR UPDATE").AsNoTracking().ToListAsync(ct);
        if (patients.Count == 0)
        {
            throw new AppException(StatusCodes.Status400BadRequest, "unknown_patient", "مریض پیدا نشد.");
        }

        if (!await db.Departments.AnyAsync(d => d.Id == departmentId && d.IsActive, ct))
        {
            throw new AppException(StatusCodes.Status400BadRequest, "unknown_department", "بخش انتخاب‌شده معتبر نیست.");
        }

        if (doctorStaffId is { } doctorId && !await db.Staff.AnyAsync(s => s.Id == doctorId && s.IsDoctor && s.IsActive, ct))
        {
            throw new AppException(StatusCodes.Status400BadRequest, "unknown_doctor", "داکتر انتخاب‌شده معتبر نیست.");
        }
    }

    /// <summary>
    /// Every step of a patient's day happens inside one open visit, so a second fee is never owed.
    /// Read on a separate connection. This transaction's snapshot predates the wait for the patient lock, so it would miss
    /// the visit the first desk just committed; a locking read would take gap locks that deadlock every desk's insert.
    /// </summary>
    private async Task EnsureNoActiveVisitAsync(long patientId, CancellationToken ct)
    {
        var today = KabulClock.Today;
        await using var fresh = new AppDbContext(options);
        var active = await fresh.Visits.AsNoTracking().AnyAsync(v =>
            v.PatientId == patientId && v.VisitDate == today &&
            v.Status != VisitStatus.Completed && v.Status != VisitStatus.Cancelled, ct);
        if (active)
        {
            throw new AppException(StatusCodes.Status409Conflict, "duplicate_visit", "این مریض امروز مراجعه فعال دارد. ادامه کار در همان مراجعه انجام شود.");
        }
    }

    private async Task ChargeExtraServiceAsync(Visit visit, int? serviceId, CancellationToken ct)
    {
        if (serviceId is not { } id)
        {
            return;
        }

        var service = await db.Services.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id && s.IsActive, ct)
            ?? throw new AppException(StatusCodes.Status400BadRequest, "unknown_service", "خدمت انتخاب‌شده معتبر نیست.");
        var price = await FinanceEndpoints.CurrentPriceAsync(db, id, ct);
        await invoices.ChargeVisitAsync(visit.PatientId, visit.Id, new ChargeLine(service.Name, 1, price, Accounts.ServiceRevenue, id, nameof(Visit), visit.Id), ct);
    }

    /// <summary>Completes a token issued before the patient-first flow. Must run inside a transaction.</summary>
    public async Task<Visit> CreateAsync(CreateVisitRequest request, CancellationToken ct)
    {
        await LockPatientAndValidateAsync(request.PatientId, request.DepartmentId, request.DoctorStaffId, ct);

        if (request.TokenId is not { } tokenId)
        {
            throw FeeRequired();
        }

        var token = await db.VisitTokens.FromSqlInterpolated($"SELECT * FROM VisitTokens WHERE Id = {tokenId} FOR UPDATE").SingleOrDefaultAsync(ct)
            ?? throw FeeRequired();
        if (token.VisitId is not null)
        {
            throw new AppException(StatusCodes.Status409Conflict, "token_used", "این نوبت قبلاً برای مراجعه‌ای استفاده شده است.");
        }

        var feeInvoice = await invoices.LockAsync(token.InvoiceId, ct);
        if (!InvoiceService.IsSettled(feeInvoice))
        {
            throw FeeRequired();
        }

        await EnsureNoActiveVisitAsync(request.PatientId, ct);
        var today = KabulClock.Today;

        // The number on the patient's slip stays theirs for the whole visit, even in another department, unless the token is from another day.
        var keepsNumber = token.TokenDate == today && token.QueueNumber is not null;

        var visit = new Visit
        {
            PatientId = request.PatientId,
            DepartmentId = request.DepartmentId,
            DoctorStaffId = request.DoctorStaffId ?? token.DoctorStaffId,
            Reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim(),
            VisitDate = today,
            VisitNumber = $"V-{await numbers.NextAsync("visit", ct: ct):D7}",
            QueueNumber = keepsNumber ? token.QueueNumber : (int)await numbers.NextAsync(QueueNumberScope, today.ToString("yyyyMMdd"), ct),
            CreatedByUserId = user.Id,
        };
        db.Visits.Add(visit);
        await db.SaveChangesAsync(ct);

        token.VisitId = visit.Id;
        feeInvoice.PatientId = visit.PatientId;
        feeInvoice.VisitId = visit.Id;

        db.VisitStatusHistory.Add(new VisitStatusHistory { VisitId = visit.Id, ToStatus = visit.Status, ChangedByUserId = user.Id });
        audit.Add("VisitCreated", "Visit", visit.Id.ToString());
        outbox.Add("VisitCreated", new { visitId = visit.Id }, EventGroups);
        await db.SaveChangesAsync(ct);

        await ChargeExtraServiceAsync(visit, request.ServiceId, ct);
        return visit;
    }

    private static AppException FeeRequired() =>
        new(StatusCodes.Status409Conflict, "visit_fee_required", "فیس ویزیت اجباری است. ابتدا پذیرش حضوری را آغاز و فیس را کامل دریافت کنید.");

    public async Task<VisitRow> LoadRowAsync(long id, CancellationToken ct) =>
        (await QueryRows(db, db.Visits.AsNoTracking().Where(v => v.Id == id)).ToListAsync(ct)).Select(ToRow).SingleOrDefault()
        ?? throw AppException.NotFound();

    public static IQueryable<VisitProjection> QueryRows(AppDbContext db, IQueryable<Visit> visits) => visits.Select(v => new VisitProjection(
        v.Id, v.VisitNumber, v.QueueNumber, v.PatientId, v.Patient!.FileNumber, v.Patient.FirstName, v.Patient.LastName,
        v.DepartmentId, v.Department!.Name, v.Doctor != null ? v.Doctor.FullName : null, v.Status, v.Reason, v.VisitDate, v.CreatedAt, v.Version, v.Patient.Gender, v.DoctorStaffId,
        db.PreExaminations.Any(p => p.VisitId == v.Id && p.CompletedAt != null),
        db.VisitTokens.Where(t => t.VisitId == v.Id).Select(t => t.Invoice!.Status == InvoiceStatus.Void ? "Unpaid"
            : t.Invoice.Total - t.Invoice.Discount <= t.Invoice.Paid - t.Invoice.Refunded ? "Paid"
            : t.Invoice.Paid > 0 && t.Invoice.Paid == t.Invoice.Refunded ? "Refunded"
            : "Unpaid").FirstOrDefault()));

    public static VisitRow ToRow(VisitProjection v) => new(
        v.Id, v.VisitNumber, v.QueueNumber, v.PatientId, v.FileNumber, $"{v.FirstName} {v.LastName}".Trim(),
        v.DepartmentId, v.DepartmentName, v.DoctorName, v.Status.ToString(), v.Reason, v.VisitDate, v.CreatedAt, v.Version,
        VisitWorkflow.NextOf(v.Status, v.VisionComplete).Select(s => s.ToString()).ToList(), v.Gender.ToString(), v.DoctorStaffId, v.FeeStatus ?? "None");
}

public sealed record VisitProjection(
    long Id, string VisitNumber, int? QueueNumber, long PatientId, string FileNumber, string FirstName, string? LastName,
    int DepartmentId, string DepartmentName, string? DoctorName, VisitStatus Status, string? Reason, DateOnly VisitDate, DateTime CreatedAt, int Version, Patients.Gender Gender, long? DoctorStaffId, bool VisionComplete, string? FeeStatus);

public static class VisitEndpoints
{
    public static void MapVisits(this IEndpointRouteBuilder app)
    {
        var visits = app.MapGroup("/api/visits").WithTags("Visits");

        visits.MapGet("/", async (AppDbContext db, DateOnly? date, int? departmentId, long? patientId, int page = 1, int pageSize = 50, CancellationToken ct = default) =>
        {
            pageSize = Math.Clamp(pageSize, 1, 100);
            page = Math.Max(page, 1);

            var query = db.Visits.AsNoTracking();
            if (patientId is { } pid)
            {
                query = query.Where(v => v.PatientId == pid);
            }
            else
            {
                var day = date ?? KabulClock.Today;
                query = query.Where(v => v.VisitDate == day);
            }

            if (departmentId is { } dept)
            {
                query = query.Where(v => v.DepartmentId == dept);
            }

            var total = await query.CountAsync(ct);
            var ordered = patientId is null ? query.OrderBy(v => v.DepartmentId).ThenBy(v => v.QueueNumber) : query.OrderByDescending(v => v.Id);
            var rows = await VisitService.QueryRows(db, ordered.Skip((page - 1) * pageSize).Take(pageSize)).ToListAsync(ct);
            return Results.Ok(new PagedResult<VisitRow>(rows.Select(VisitService.ToRow).ToList(), total, page, pageSize));
        }).RequirePermission("visits.view");

        visits.MapGet("/{id:long}", async (long id, VisitService service, CancellationToken ct) =>
            Results.Ok(await service.LoadRowAsync(id, ct))).RequirePermission("visits.view");

        visits.MapPost("/", async (CreateVisitRequest request, Idempotency idempotency, VisitService service, CancellationToken ct) =>
            await idempotency.RunAsync(request, async () =>
            {
                var visit = await service.CreateAsync(request, ct);
                return (StatusCodes.Status201Created, await service.LoadRowAsync(visit.Id, ct));
            }, ct)).RequirePermission("visits.create");

        visits.MapPost("/{id:long}/status", async (long id, ChangeVisitStatusRequest request, AppDbContext db, ClinicalAccess access, VisitService service, VisitFeeService visitFees, InvoiceService invoices, CurrentUser user, AuditService audit, Outbox outbox, IAuthorizationService authorization, HttpContext http, CancellationToken ct) =>
        {
            var needed = request.Status == VisitStatus.Cancelled ? "visits.cancel" : "visits.edit";
            if (!http.User.HasClaim(RolePermissions.ClaimType, needed))
            {
                throw new AppException(StatusCodes.Status403Forbidden, "forbidden", "شما اجازه این عملیات را ندارید.");
            }

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var visit = await VisitService.LockAsync(db, id, ct);
            db.Entry(visit).Property(v => v.Version).OriginalValue = request.Version;
            await access.EnsureResponsibleAsync(id, ct);

            if (!VisitWorkflow.CanMove(visit.Status, request.Status))
            {
                throw new AppException(StatusCodes.Status409Conflict, "invalid_transition", "این تغییر وضعیت برای مراجعه مجاز نیست.");
            }

            if (VisitWorkflow.NeedsVision(request.Status))
            {
                await ClinicalEndpoints.EnsureVisionCompleteAsync(db, id, ct);
            }

            if (request.Status == VisitStatus.InConsultation)
            {
                await DiagnosticGate.EnsureChargePaidAsync(db, nameof(Visit), id, ct);
            }

            if (request.Status == VisitStatus.Completed)
            {
                await DiagnosticGate.EnsureNonePendingAsync(db, id, ct);
            }
            else if (request.Status == VisitStatus.Cancelled)
            {
                // Otherwise an unpaid request of a cancelled visit stays on the fee list and can still be charged.
                await DiagnosticGate.CloseOpenPrepaidAsync(db, invoices, audit, id, ct);
                await visitFees.CloseUnpaidFeeAsync(id, ct);
            }

            db.VisitStatusHistory.Add(new VisitStatusHistory { VisitId = id, FromStatus = visit.Status, ToStatus = request.Status, ChangedByUserId = user.Id });
            visit.Status = request.Status;
            audit.Add("VisitStatusChanged", "Visit", id.ToString(), request.Status.ToString());
            outbox.Add("VisitStatusChanged", new { visitId = id }, VisitService.EventGroups);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Ok(await service.LoadRowAsync(id, ct));
        }).RequireAuthorization();

        // The responsible doctor hands over their own patient; visits.manage covers a doctor who cannot. The record stays where it is and the reason stays in the audit.
        visits.MapPost("/{id:long}/transfer-doctor", async (long id, TransferDoctorRequest request, AppDbContext db, ClinicalAccess access, VisitService service, CurrentUser user, AuditService audit, Outbox outbox, HttpContext http, CancellationToken ct) =>
        {
            var reason = request.Reason.Trim();
            if (reason.Length < 3)
            {
                throw new AppException(StatusCodes.Status400BadRequest, "reason_required", "دلیل انتقال را بنویسید.");
            }

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var visit = await VisitService.LockAsync(db, id, ct);
            db.Entry(visit).Property(v => v.Version).OriginalValue = request.Version;

            if (!http.User.HasClaim(RolePermissions.ClaimType, "visits.manage") && await access.ResponsibleUserAsync(id, ct) != user.Id)
            {
                throw new AppException(StatusCodes.Status403Forbidden, "transfer_not_allowed", "فقط داکتر مسئول این مریض یا مدیر مجاز می‌تواند مسئولیت را انتقال دهد.");
            }

            if (visit.Status is VisitStatus.Completed or VisitStatus.Cancelled)
            {
                throw new AppException(StatusCodes.Status409Conflict, "visit_closed", "این مراجعه بسته شده است.");
            }

            var target = await db.Staff.AsNoTracking().Where(s => s.Id == request.DoctorStaffId && s.IsDoctor && s.IsActive).Select(s => new { s.Id, s.UserId }).SingleOrDefaultAsync(ct)
                ?? throw new AppException(StatusCodes.Status400BadRequest, "unknown_doctor", "داکتر انتخاب‌شده معتبر نیست.");

            // With no account nobody would answer for the patient, and whoever started the exam would keep them.
            if (target.UserId is null)
            {
                throw new AppException(StatusCodes.Status409Conflict, "doctor_no_account", "این داکتر حساب کاربری ندارد و مسئولیت به او انتقال نمی‌یابد.");
            }

            if (visit.DoctorStaffId == target.Id)
            {
                throw new AppException(StatusCodes.Status409Conflict, "same_doctor", "این مریض همین حالا به همین داکتر سپرده شده است.");
            }

            audit.Add("VisitDoctorTransferred", "Visit", id.ToString(), $"{visit.DoctorStaffId?.ToString() ?? "-"} => {target.Id}: {reason}");
            visit.DoctorStaffId = target.Id;
            outbox.Add("VisitStatusChanged", new { visitId = id }, VisitService.EventGroups);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Ok(await service.LoadRowAsync(id, ct));
        }).RequireAuthorization();

        var appointments = app.MapGroup("/api/appointments").WithTags("Appointments");

        appointments.MapGet("/", async (AppDbContext db, DateOnly? date, CancellationToken ct) =>
        {
            // ScheduledAt is UTC; a Kabul calendar day runs from 19:30 UTC of the previous day.
            var day = date ?? KabulClock.Today;
            var from = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddHours(-4.5);
            var to = from.AddDays(1);

            var rows = await db.Appointments.AsNoTracking()
                .Where(a => a.ScheduledAt >= from && a.ScheduledAt < to)
                .OrderBy(a => a.ScheduledAt)
                .Take(300)
                .Select(a => new { a.Id, a.PatientId, a.Patient!.FileNumber, a.Patient.FirstName, a.Patient.LastName, a.DoctorStaffId, DoctorName = a.Doctor!.FullName, a.ScheduledAt, a.Status, a.Note, a.VisitId, a.Version })
                .ToListAsync(ct);

            return Results.Ok(rows.Select(a => new AppointmentRow(a.Id, a.PatientId, a.FileNumber, $"{a.FirstName} {a.LastName}".Trim(), a.DoctorStaffId, a.DoctorName, a.ScheduledAt, a.Status.ToString(), a.Note, a.VisitId, a.Version)).ToList());
        }).RequirePermission("appointments.view");

        appointments.MapPost("/", async (CreateAppointmentRequest request, AppDbContext db, Idempotency idempotency, CurrentUser user, AuditService audit, CancellationToken ct) =>
            await idempotency.RunAsync(request, async () =>
            {
                var at = request.ScheduledAt.Kind == DateTimeKind.Utc ? request.ScheduledAt : request.ScheduledAt.ToUniversalTime();
                if (at < DateTime.UtcNow)
                {
                    throw new AppException(StatusCodes.Status400BadRequest, "appointment_in_past", "زمان وقت ملاقات نمی‌تواند در گذشته باشد.");
                }

                if (!await db.Patients.AnyAsync(p => p.Id == request.PatientId, ct))
                {
                    throw new AppException(StatusCodes.Status400BadRequest, "unknown_patient", "مریض پیدا نشد.");
                }

                if (!await db.Staff.AnyAsync(s => s.Id == request.DoctorStaffId && s.IsDoctor && s.IsActive, ct))
                {
                    throw new AppException(StatusCodes.Status400BadRequest, "unknown_doctor", "داکتر انتخاب‌شده معتبر نیست.");
                }

                if (await db.Appointments.AnyAsync(a => a.DoctorStaffId == request.DoctorStaffId && a.ScheduledAt == at && a.Status == AppointmentStatus.Scheduled, ct))
                {
                    throw new AppException(StatusCodes.Status409Conflict, "appointment_taken", "این زمان برای این داکتر قبلاً وقت ملاقات دارد.");
                }

                var appointment = new Appointment { PatientId = request.PatientId, DoctorStaffId = request.DoctorStaffId, ScheduledAt = at, Note = Text.Clean(request.Note), CreatedByUserId = user.Id };
                db.Appointments.Add(appointment);
                await db.SaveChangesAsync(ct);
                audit.Add("AppointmentCreated", "Appointment", appointment.Id.ToString());
                await db.SaveChangesAsync(ct);
                return (StatusCodes.Status201Created, new { appointment.Id });
            }, ct)).RequirePermission("appointments.create");

        appointments.MapPost("/{id:long}/cancel", async (long id, int version, AppDbContext db, AuditService audit, CancellationToken ct) =>
        {
            var appointment = await db.Appointments.SingleOrDefaultAsync(a => a.Id == id, ct) ?? throw AppException.NotFound();
            db.Entry(appointment).Property(a => a.Version).OriginalValue = version;
            if (appointment.Status != AppointmentStatus.Scheduled)
            {
                throw new AppException(StatusCodes.Status409Conflict, "invalid_transition", "این وقت ملاقات قابل لغو نیست.");
            }

            appointment.Status = AppointmentStatus.Cancelled;
            audit.Add("AppointmentCancelled", "Appointment", id.ToString());
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequirePermission("appointments.cancel");

        // Arrival opens the same visit a walk-in gets. The fee and the queue number follow in the same payment step.
        appointments.MapPost("/{id:long}/check-in", async (long id, CheckInRequest request, AppDbContext db, Idempotency idempotency, VisitService service, VisitFeeService visitFees, AuditService audit, CancellationToken ct) =>
            await idempotency.RunAsync(new { id, request }, async () =>
            {
                var appointment = await db.Appointments.FromSqlInterpolated($"SELECT * FROM Appointments WHERE Id = {id} FOR UPDATE").SingleOrDefaultAsync(ct) ?? throw AppException.NotFound();
                if (appointment.Version != request.Version)
                {
                    throw new AppException(StatusCodes.Status409Conflict, "record_changed", "این وقت قبلی همین حالا تغییر کرده است. فهرست را تازه کنید و دوباره تلاش نمایید.");
                }
                if (appointment.Status != AppointmentStatus.Scheduled)
                {
                    throw new AppException(StatusCodes.Status409Conflict, "invalid_transition", "برای این وقت ملاقات قبلاً پذیرش انجام شده یا لغو شده است.");
                }

                var visit = await service.AdmitAsync(new CreateAdmissionRequest(appointment.PatientId, request.DepartmentId, appointment.DoctorStaffId, appointment.Note), ct);
                appointment.Status = AppointmentStatus.CheckedIn;
                appointment.VisitId = visit.Id;
                audit.Add("AppointmentCheckedIn", "Appointment", appointment.Id.ToString(), $"Visit {visit.Id}");
                await db.SaveChangesAsync(ct);
                return (StatusCodes.Status201Created, await visitFees.LoadAdmissionAsync(visit.Id, ct));
            }, ct)).RequirePermission("visits.create").RequirePermission("visitfees.create");

        app.MapGet("/api/lookups/departments", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Departments.AsNoTracking().Where(d => d.IsActive).OrderBy(d => d.Id).Select(d => new LookupItem(d.Id, d.Name)).ToListAsync(ct)))
            .RequireAuthorization().WithTags("Lookups");

        app.MapGet("/api/lookups/doctors", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Staff.AsNoTracking().Where(s => s.IsDoctor && s.IsActive).OrderBy(s => s.FullName).Select(s => new LookupItem(s.Id, s.FullName)).ToListAsync(ct)))
            .RequireAuthorization().WithTags("Lookups");
    }
}

public sealed record CheckInRequest(int DepartmentId, int Version);
