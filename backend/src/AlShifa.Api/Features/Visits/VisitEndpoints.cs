using System.ComponentModel.DataAnnotations;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Finance;
using AlShifa.Api.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace AlShifa.Api.Features.Visits;

public sealed record CreateVisitRequest(long PatientId, int DepartmentId, long? DoctorStaffId, [MaxLength(300)] string? Reason, int? ServiceId = null);

public sealed record VisitRow(
    long Id, string VisitNumber, int QueueNumber, long PatientId, string PatientFileNumber, string PatientName,
    int DepartmentId, string DepartmentName, string? DoctorName, string Status, string? Reason, DateOnly VisitDate,
    DateTime CreatedAt, int Version, IReadOnlyList<string> NextStatuses, string PatientGender);

public sealed record ChangeVisitStatusRequest(VisitStatus Status, int Version);

public sealed record CreateAppointmentRequest(long PatientId, long DoctorStaffId, DateTime ScheduledAt, [MaxLength(300)] string? Note);

public sealed record AppointmentRow(long Id, long PatientId, string PatientFileNumber, string PatientName, long DoctorStaffId, string DoctorName, DateTime ScheduledAt, string Status, string? Note, long? VisitId, int Version);

public sealed record LookupItem(long Id, string Name);

public sealed class VisitService(AppDbContext db, NumberService numbers, CurrentUser user, AuditService audit, Outbox outbox, InvoiceService invoices)
{
    public static readonly string[] EventGroups = [Groups.Reception, Groups.Triage, Groups.Doctor, Groups.Management];

    /// <summary>Must run inside a transaction. The stored ActiveVisitKey unique index rejects concurrent duplicates.</summary>
    public async Task<Visit> CreateAsync(CreateVisitRequest request, CancellationToken ct)
    {
        if (!await db.Patients.AnyAsync(p => p.Id == request.PatientId, ct))
        {
            throw new AppException(StatusCodes.Status400BadRequest, "unknown_patient", "مریض پیدا نشد.");
        }

        if (!await db.Departments.AnyAsync(d => d.Id == request.DepartmentId && d.IsActive, ct))
        {
            throw new AppException(StatusCodes.Status400BadRequest, "unknown_department", "بخش انتخاب‌شده معتبر نیست.");
        }

        if (request.DoctorStaffId is { } doctorId && !await db.Staff.AnyAsync(s => s.Id == doctorId && s.IsDoctor && s.IsActive, ct))
        {
            throw new AppException(StatusCodes.Status400BadRequest, "unknown_doctor", "داکتر انتخاب‌شده معتبر نیست.");
        }

        var today = KabulClock.Today;
        var active = await db.Visits.AnyAsync(v =>
            v.PatientId == request.PatientId && v.DepartmentId == request.DepartmentId && v.VisitDate == today &&
            v.Status != VisitStatus.Completed && v.Status != VisitStatus.Cancelled, ct);
        if (active)
        {
            throw new AppException(StatusCodes.Status409Conflict, "duplicate_visit", "این مریض امروز در همین بخش مراجعه فعال دارد.");
        }

        var visit = new Visit
        {
            PatientId = request.PatientId,
            DepartmentId = request.DepartmentId,
            DoctorStaffId = request.DoctorStaffId,
            Reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim(),
            VisitDate = today,
            VisitNumber = $"V-{await numbers.NextAsync("visit", ct: ct):D7}",
            QueueNumber = (int)await numbers.NextAsync($"queue:{request.DepartmentId}", today.ToString("yyyyMMdd"), ct),
            CreatedByUserId = user.Id,
        };
        db.Visits.Add(visit);
        await db.SaveChangesAsync(ct);

        db.VisitStatusHistory.Add(new VisitStatusHistory { VisitId = visit.Id, ToStatus = visit.Status, ChangedByUserId = user.Id });
        audit.Add("VisitCreated", "Visit", visit.Id.ToString());
        outbox.Add("VisitCreated", new { visitId = visit.Id }, EventGroups);
        await db.SaveChangesAsync(ct);

        if (request.ServiceId is { } serviceId)
        {
            var service = await db.Services.AsNoTracking().SingleOrDefaultAsync(s => s.Id == serviceId && s.IsActive, ct)
                ?? throw new AppException(StatusCodes.Status400BadRequest, "unknown_service", "خدمت انتخاب‌شده معتبر نیست.");
            var price = await FinanceEndpoints.CurrentPriceAsync(db, serviceId, ct);
            await invoices.ChargeVisitAsync(visit.PatientId, visit.Id, new ChargeLine(service.Name, 1, price, Accounts.ServiceRevenue, serviceId, nameof(Visit), visit.Id), ct);
        }

        return visit;
    }

    public async Task<VisitRow> LoadRowAsync(long id, CancellationToken ct) =>
        (await QueryRows(db.Visits.AsNoTracking().Where(v => v.Id == id)).ToListAsync(ct)).Select(ToRow).SingleOrDefault()
        ?? throw AppException.NotFound();

    public static IQueryable<VisitProjection> QueryRows(IQueryable<Visit> visits) => visits.Select(v => new VisitProjection(
        v.Id, v.VisitNumber, v.QueueNumber, v.PatientId, v.Patient!.FileNumber, v.Patient.FirstName, v.Patient.LastName,
        v.DepartmentId, v.Department!.Name, v.Doctor != null ? v.Doctor.FullName : null, v.Status, v.Reason, v.VisitDate, v.CreatedAt, v.Version, v.Patient.Gender));

    public static VisitRow ToRow(VisitProjection v) => new(
        v.Id, v.VisitNumber, v.QueueNumber, v.PatientId, v.FileNumber, $"{v.FirstName} {v.LastName}".Trim(),
        v.DepartmentId, v.DepartmentName, v.DoctorName, v.Status.ToString(), v.Reason, v.VisitDate, v.CreatedAt, v.Version,
        VisitWorkflow.NextOf(v.Status).Select(s => s.ToString()).ToList(), v.Gender.ToString());
}

public sealed record VisitProjection(
    long Id, string VisitNumber, int QueueNumber, long PatientId, string FileNumber, string FirstName, string? LastName,
    int DepartmentId, string DepartmentName, string? DoctorName, VisitStatus Status, string? Reason, DateOnly VisitDate, DateTime CreatedAt, int Version, Patients.Gender Gender);

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
            var rows = await VisitService.QueryRows(ordered.Skip((page - 1) * pageSize).Take(pageSize)).ToListAsync(ct);
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

        visits.MapPost("/{id:long}/status", async (long id, ChangeVisitStatusRequest request, AppDbContext db, VisitService service, CurrentUser user, AuditService audit, Outbox outbox, IAuthorizationService authorization, HttpContext http, CancellationToken ct) =>
        {
            var needed = request.Status == VisitStatus.Cancelled ? "visits.cancel" : "visits.edit";
            if (!http.User.HasClaim(RolePermissions.ClaimType, needed))
            {
                throw new AppException(StatusCodes.Status403Forbidden, "forbidden", "شما اجازه این عملیات را ندارید.");
            }

            var visit = await db.Visits.SingleOrDefaultAsync(v => v.Id == id, ct) ?? throw AppException.NotFound();
            db.Entry(visit).Property(v => v.Version).OriginalValue = request.Version;

            if (!VisitWorkflow.CanMove(visit.Status, request.Status))
            {
                throw new AppException(StatusCodes.Status409Conflict, "invalid_transition", "این تغییر وضعیت برای مراجعه مجاز نیست.");
            }

            db.VisitStatusHistory.Add(new VisitStatusHistory { VisitId = id, FromStatus = visit.Status, ToStatus = request.Status, ChangedByUserId = user.Id });
            visit.Status = request.Status;
            audit.Add("VisitStatusChanged", "Visit", id.ToString(), request.Status.ToString());
            outbox.Add("VisitStatusChanged", new { visitId = id }, VisitService.EventGroups);
            await db.SaveChangesAsync(ct);
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
                    throw new AppException(StatusCodes.Status400BadRequest, "appointment_in_past", "زمان نوبت نمی‌تواند در گذشته باشد.");
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
                    throw new AppException(StatusCodes.Status409Conflict, "appointment_taken", "این زمان برای این داکتر قبلاً نوبت دارد.");
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
                throw new AppException(StatusCodes.Status409Conflict, "invalid_transition", "این نوبت قابل لغو نیست.");
            }

            appointment.Status = AppointmentStatus.Cancelled;
            audit.Add("AppointmentCancelled", "Appointment", id.ToString());
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequirePermission("appointments.cancel");

        appointments.MapPost("/{id:long}/check-in", async (long id, CheckInRequest request, AppDbContext db, Idempotency idempotency, VisitService service, CancellationToken ct) =>
            await idempotency.RunAsync(new { id, request }, async () =>
            {
                var appointment = await db.Appointments.SingleOrDefaultAsync(a => a.Id == id, ct) ?? throw AppException.NotFound();
                db.Entry(appointment).Property(a => a.Version).OriginalValue = request.Version;
                if (appointment.Status != AppointmentStatus.Scheduled)
                {
                    throw new AppException(StatusCodes.Status409Conflict, "invalid_transition", "برای این نوبت قبلاً پذیرش انجام شده یا لغو شده است.");
                }

                var visit = await service.CreateAsync(new CreateVisitRequest(appointment.PatientId, request.DepartmentId, appointment.DoctorStaffId, appointment.Note), ct);
                appointment.Status = AppointmentStatus.CheckedIn;
                appointment.VisitId = visit.Id;
                await db.SaveChangesAsync(ct);
                return (StatusCodes.Status201Created, await service.LoadRowAsync(visit.Id, ct));
            }, ct)).RequirePermission("visits.create");

        app.MapGet("/api/lookups/departments", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Departments.AsNoTracking().Where(d => d.IsActive).OrderBy(d => d.Id).Select(d => new LookupItem(d.Id, d.Name)).ToListAsync(ct)))
            .RequireAuthorization().WithTags("Lookups");

        app.MapGet("/api/lookups/doctors", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Staff.AsNoTracking().Where(s => s.IsDoctor && s.IsActive).OrderBy(s => s.FullName).Select(s => new LookupItem(s.Id, s.FullName)).ToListAsync(ct)))
            .RequireAuthorization().WithTags("Lookups");
    }
}

public sealed record CheckInRequest(int DepartmentId, int Version);
