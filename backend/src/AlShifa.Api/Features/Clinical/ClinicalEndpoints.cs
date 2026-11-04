using System.ComponentModel.DataAnnotations;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Visits;
using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AlShifa.Api.Features.Clinical;

public sealed record SavePreExamRequest(
    [MaxLength(500)] string? ChiefComplaint,
    [MaxLength(1000)] string? History,
    [MaxLength(30)] string? VisualAcuityOd,
    [MaxLength(30)] string? VisualAcuityOs,
    [MaxLength(30)] string? IopOd,
    [MaxLength(30)] string? IopOs,
    [MaxLength(1000)] string? Measurements,
    [MaxLength(2000)] string? Notes,
    int? Version);

public sealed record PreExamDto(
    long Id, long VisitId, string? ChiefComplaint, string? History, string? VisualAcuityOd, string? VisualAcuityOs,
    string? IopOd, string? IopOs, string? Measurements, string? Notes, DateTime RecordedAt, DateTime? CompletedAt, int Version);

public sealed record DiagnosisInput(EyeSide Eye, [Required, MaxLength(500)] string Text);

public sealed record SaveConsultationRequest(
    [MaxLength(4000)] string? FindingsOd,
    [MaxLength(4000)] string? FindingsOs,
    [MaxLength(4000)] string? TreatmentPlan,
    [MaxLength(4000)] string? ConfidentialNote,
    DiagnosisInput[]? Diagnoses,
    int? Version);

public sealed record FinalizeRequest(int Version, bool CloseVisit);

public sealed record ReopenConsultationRequest([Required, MinLength(3), MaxLength(500)] string Reason, int Version);

public sealed record AmendRequest([Required, MaxLength(40)] string Field, [MaxLength(4000)] string? Value, [Required, MinLength(3), MaxLength(500)] string Reason, int Version);

public sealed record EmergencyAccessRequest([Required, MinLength(5), MaxLength(500)] string Reason);

public sealed record ConsultationDto(
    long Id, long VisitId, string? FindingsOd, string? FindingsOs, string? TreatmentPlan, string? ConfidentialNote,
    IReadOnlyList<DiagnosisDto> Diagnoses, DateTime? FinalizedAt, int Version);

public sealed record DiagnosisDto(string Eye, string Text);

public sealed record AmendmentDto(string Field, string? OldValue, string? NewValue, string Reason, string? ByUser, DateTime CreatedAt);

public sealed record RxItemInput(
    [Required, MaxLength(200)] string DrugName,
    [MaxLength(100)] string? Dose,
    [MaxLength(100)] string? Route,
    [MaxLength(100)] string? Duration,
    [MaxLength(300)] string? Instructions,
    [Range(0.001, 100000)] decimal Quantity);

public sealed record CreatePrescriptionRequest(long VisitId, [MaxLength(1000)] string? Notes, [Required, MinLength(1)] RxItemInput[] Items);

public sealed record RxItemDto(long Id, string DrugName, string? Dose, string? Route, string? Duration, string? Instructions, decimal Quantity, decimal DispensedQuantity);

public sealed record PrescriptionDto(long Id, string RxNumber, long PatientId, string PatientFileNumber, string PatientName, long VisitId, string? Notes, string Status, DateTime CreatedAt, int Version, IReadOnlyList<RxItemDto> Items);

public sealed record CreateOpticalRxRequest(
    long VisitId,
    [MaxLength(10)] string? SphereOd, [MaxLength(10)] string? CylinderOd, [MaxLength(10)] string? AxisOd, [MaxLength(10)] string? AddOd,
    [MaxLength(10)] string? SphereOs, [MaxLength(10)] string? CylinderOs, [MaxLength(10)] string? AxisOs, [MaxLength(10)] string? AddOs,
    [MaxLength(10)] string? Pd, [MaxLength(1000)] string? Notes);

public sealed record OpticalRxDto(
    long Id, string RxNumber, long PatientId, string PatientFileNumber, string PatientName, long VisitId,
    string? SphereOd, string? CylinderOd, string? AxisOd, string? AddOd, string? SphereOs, string? CylinderOs, string? AxisOs, string? AddOs,
    string? Pd, string? Notes, bool IsCancelled, DateTime CreatedAt, int Version);

public sealed record CreateFollowUpRequest(long VisitId, DateOnly DueDate, [MaxLength(300)] string? Note);

public sealed record FollowUpDto(long Id, DateOnly DueDate, string? Note);

public sealed record ChartDto(
    VisitRow Visit, PreExamDto? PreExam, ConsultationDto? Consultation, IReadOnlyList<AmendmentDto> Amendments,
    IReadOnlyList<PrescriptionDto> Prescriptions, IReadOnlyList<OpticalRxDto> OpticalPrescriptions,
    IReadOnlyList<DiagnosticOrderDto> DiagnosticOrders, IReadOnlyList<FollowUpDto> FollowUps);

public sealed record HistoryItem(long VisitId, DateOnly VisitDate, string DepartmentName, string? DoctorName, string? TreatmentPlan, IReadOnlyList<DiagnosisDto> Diagnoses);

public static class ClinicalEndpoints
{
    private static readonly string[] AmendableFields = ["FindingsOd", "FindingsOs", "TreatmentPlan", "ConfidentialNote"];

    public static void MapClinical(this IEndpointRouteBuilder app)
    {
        var preExam = app.MapGroup("/api/pre-examinations").WithTags("PreExam");

        preExam.MapGet("/{visitId:long}", async (long visitId, AppDbContext db, ClinicalAccess access, CancellationToken ct) =>
        {
            var patientId = await PatientOfVisitAsync(db, visitId, ct);
            await access.EnsureCanViewAsync(patientId, ct);
            var exam = await db.PreExaminations.AsNoTracking().SingleOrDefaultAsync(p => p.VisitId == visitId, ct);
            return exam is null ? Results.NoContent() : Results.Ok(ToDto(exam));
        }).RequirePermission("preexam.view");

        preExam.MapPut("/{visitId:long}", async (long visitId, SavePreExamRequest request, AppDbContext db, CurrentUser user, AuditService audit, CancellationToken ct) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            EnsureNotCancelled(await VisitService.LockAsync(db, visitId, ct));
            var exam = await db.PreExaminations.SingleOrDefaultAsync(p => p.VisitId == visitId, ct);
            if (exam is null)
            {
                exam = new PreExamination { VisitId = visitId, RecordedByUserId = user.Id };
                db.PreExaminations.Add(exam);
            }
            else
            {
                db.Entry(exam).Property(p => p.Version).OriginalValue = request.Version ?? throw StaleScreen();
                if (exam.CompletedAt is not null)
                {
                    throw new AppException(StatusCodes.Status409Conflict, "already_completed", "این معاینه تکمیل شده و قابل ویرایش نیست.");
                }
            }

            exam.ChiefComplaint = Text.Clean(request.ChiefComplaint);
            exam.History = Text.Clean(request.History);
            exam.VisualAcuityOd = Text.Clean(request.VisualAcuityOd);
            exam.VisualAcuityOs = Text.Clean(request.VisualAcuityOs);
            exam.IopOd = Text.Clean(request.IopOd);
            exam.IopOs = Text.Clean(request.IopOs);
            exam.Measurements = Text.Clean(request.Measurements);
            exam.Notes = Text.Clean(request.Notes);
            audit.Add("PreExamSaved", "Visit", visitId.ToString());
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Ok(ToDto(exam));
        }).RequirePermission("preexam.edit");

        preExam.MapPost("/{visitId:long}/complete", async (long visitId, AppDbContext db, AuditService audit, Outbox outbox, CurrentUser user, CancellationToken ct) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var visit = await VisitService.LockAsync(db, visitId, ct);
            EnsureNotCancelled(visit);
            var exam = await db.PreExaminations.SingleOrDefaultAsync(p => p.VisitId == visitId, ct)
                ?? throw new AppException(StatusCodes.Status409Conflict, "nothing_to_complete", "ابتدا معاینه را ذخیره کنید.");

            // An empty form must not count as Vision, or completing it would be the way around Vision.
            if (exam.VisualAcuityOd is null && exam.VisualAcuityOs is null && exam.IopOd is null && exam.IopOs is null)
            {
                throw new AppException(StatusCodes.Status409Conflict, "vision_empty", "ابتدا دید یا فشار چشم را ثبت کنید.");
            }

            exam.CompletedAt ??= DateTime.UtcNow;

            if (VisitWorkflow.CanMove(visit.Status, VisitStatus.ReadyForDoctor))
            {
                db.VisitStatusHistory.Add(new VisitStatusHistory { VisitId = visitId, FromStatus = visit.Status, ToStatus = VisitStatus.ReadyForDoctor, ChangedByUserId = user.Id });
                visit.Status = VisitStatus.ReadyForDoctor;
            }

            audit.Add("PreExamCompleted", "Visit", visitId.ToString());
            outbox.Add("PreExamCompleted", new { visitId }, Groups.Doctor, Groups.Reception, Groups.Triage);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        }).RequirePermission("preexam.edit");

        var consultations = app.MapGroup("/api/consultations").WithTags("Consultations");

        consultations.MapGet("/{visitId:long}/chart", async (long visitId, AppDbContext db, ClinicalAccess access, VisitService visits, HttpContext http, CancellationToken ct) =>
        {
            var patientId = await PatientOfVisitAsync(db, visitId, ct);
            await access.EnsureCanViewAsync(patientId, ct);

            var canSeeConfidential = http.User.HasClaim(RolePermissions.ClaimType, "consultation.edit");
            var visit = await visits.LoadRowAsync(visitId, ct);
            var exam = await db.PreExaminations.AsNoTracking().SingleOrDefaultAsync(p => p.VisitId == visitId, ct);
            var consult = await db.Consultations.AsNoTracking().SingleOrDefaultAsync(c => c.VisitId == visitId, ct);

            var amendments = new List<AmendmentDto>();
            if (consult is not null)
            {
                amendments = await (
                    from a in db.ClinicalAmendments.AsNoTracking()
                    join u in db.Users on a.UserId equals u.Id
                    where a.Entity == nameof(Consultation) && a.EntityId == consult.Id && (canSeeConfidential || a.Field != nameof(Consultation.ConfidentialNote))
                    orderby a.Id descending
                    select new AmendmentDto(a.Field, a.OldValue, a.NewValue, a.Reason, u.FullName, a.CreatedAt)).ToListAsync(ct);
            }

            var orders = await DiagnosticEndpoints.QueryOrders(db.DiagnosticOrders.AsNoTracking().Where(o => o.VisitId == visitId)).ToListAsync(ct);
            var followUps = await db.FollowUps.AsNoTracking().Where(f => f.VisitId == visitId).OrderBy(f => f.DueDate).Select(f => new FollowUpDto(f.Id, f.DueDate, f.Note)).ToListAsync(ct);

            return Results.Ok(new ChartDto(
                visit,
                exam is null ? null : ToDto(exam),
                consult is null ? null : await ToDtoAsync(db, consult, canSeeConfidential, ct),
                amendments,
                await PrescriptionEndpoints.QueryAsync(db, db.MedicalPrescriptions.Where(p => p.VisitId == visitId), ct),
                await PrescriptionEndpoints.QueryOpticalAsync(db.OpticalPrescriptions.Where(p => p.VisitId == visitId), ct),
                orders.Select(DiagnosticEndpoints.ToDto).ToList(),
                followUps));
        }).RequirePermission("consultation.view");

        consultations.MapPut("/{visitId:long}", async (long visitId, SaveConsultationRequest request, AppDbContext db, ClinicalAccess access, CurrentUser user, AuditService audit, CancellationToken ct) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            // The status history's foreign key takes a shared lock on the visit. Locking it first stops two savers deadlocking.
            var visit = await VisitService.LockAsync(db, visitId, ct);
            EnsureNotCancelled(visit);
            var consult = await db.Consultations.SingleOrDefaultAsync(c => c.VisitId == visitId, ct);
            if (consult is null)
            {
                await access.EnsureResponsibleAsync(visitId, ct);
                await EnsureVisionCompleteAsync(db, visitId, ct);
                await DiagnosticGate.EnsureChargePaidAsync(db, nameof(Visit), visitId, ct);

                consult = new Consultation { VisitId = visitId, AuthorUserId = user.Id };
                db.Consultations.Add(consult);

                // The doctor who takes an unassigned patient keeps them, so a return from refraction comes back to this doctor.
                if (visit.DoctorStaffId is null)
                {
                    var userId = user.Id;
                    visit.DoctorStaffId = await db.Staff.Where(s => s.UserId == userId && s.IsDoctor && s.IsActive).Select(s => (long?)s.Id).FirstOrDefaultAsync(ct);
                }

                if (VisitWorkflow.CanMove(visit.Status, VisitStatus.InConsultation))
                {
                    MoveVisit(db, visit, VisitStatus.InConsultation, user.Id);
                }
            }
            else
            {
                db.Entry(consult).Property(c => c.Version).OriginalValue = request.Version ?? throw StaleScreen();
                await access.EnsureResponsibleAsync(visitId, ct);
                if (consult.FinalizedAt is not null)
                {
                    throw new AppException(StatusCodes.Status409Conflict, "already_finalized", "این معاینه نهایی شده است. برای تغییر، اصلاحیه ثبت کنید.");
                }

                // Diagnoses live in their own table: without this, a save that changes only them would skip the version check.
                db.Entry(consult).State = EntityState.Modified;

                // A patient back from refraction or imaging continues the same exam.
                if (visit.Status == VisitStatus.ReadyForDoctor)
                {
                    MoveVisit(db, visit, VisitStatus.InConsultation, user.Id);
                }
            }

            consult.FindingsOd = Text.Clean(request.FindingsOd);
            consult.FindingsOs = Text.Clean(request.FindingsOs);
            consult.TreatmentPlan = Text.Clean(request.TreatmentPlan);
            consult.ConfidentialNote = Text.Clean(request.ConfidentialNote);
            audit.Add("ConsultationSaved", "Visit", visitId.ToString());
            await db.SaveChangesAsync(ct);

            if (request.Diagnoses is not null)
            {
                // Deleted by key: a range delete that finds nothing leaves a gap lock, and two doctors saving a first diagnosis deadlock on it.
                db.Diagnoses.RemoveRange(await db.Diagnoses.Where(d => d.ConsultationId == consult.Id).ToListAsync(ct));
                db.Diagnoses.AddRange(request.Diagnoses.Select(d => new Diagnosis { ConsultationId = consult.Id, Eye = d.Eye, Text = d.Text.Trim() }));
                await db.SaveChangesAsync(ct);
            }

            await tx.CommitAsync(ct);
            return Results.Ok(await ToDtoAsync(db, consult, canSeeConfidential: true, ct));
        }).RequirePermission("consultation.edit");

        consultations.MapPost("/{visitId:long}/finalize", async (long visitId, FinalizeRequest request, AppDbContext db, ClinicalAccess access, CurrentUser user, AuditService audit, Outbox outbox, CancellationToken ct) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var visit = await VisitService.LockAsync(db, visitId, ct);
            await access.EnsureResponsibleAsync(visitId, ct);
            var consult = await db.Consultations.SingleOrDefaultAsync(c => c.VisitId == visitId, ct)
                ?? throw new AppException(StatusCodes.Status409Conflict, "nothing_to_finalize", "ابتدا معاینه را ذخیره کنید.");
            db.Entry(consult).Property(c => c.Version).OriginalValue = request.Version;
            await DiagnosticGate.EnsureNonePendingAsync(db, visitId, ct);
            consult.FinalizedAt ??= DateTime.UtcNow;

            if (request.CloseVisit)
            {
                // A returned patient the doctor closes without further edits has still been seen.
                if (visit.Status == VisitStatus.ReadyForDoctor)
                {
                    MoveVisit(db, visit, VisitStatus.InConsultation, user.Id);
                }

                if (!VisitWorkflow.CanMove(visit.Status, VisitStatus.Completed))
                {
                    throw new AppException(StatusCodes.Status409Conflict, "invalid_transition", "این مراجعه در وضعیت فعلی قابل تکمیل نیست.");
                }

                MoveVisit(db, visit, VisitStatus.Completed, user.Id);
            }

            audit.Add("ConsultationFinalized", "Visit", visitId.ToString());
            outbox.Add("ConsultationCompleted", new { visitId }, Groups.Doctor, Groups.Reception, Groups.Management);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Ok(await ToDtoAsync(db, consult, canSeeConfidential: true, ct));
        }).RequirePermission("consultation.approve");

        // Findings and diagnoses stay as they were; only the lock lifts, and the reason stays on the record.
        consultations.MapPost("/{visitId:long}/reopen", async (long visitId, ReopenConsultationRequest request, AppDbContext db, ClinicalAccess access, CurrentUser user, AuditService audit, CancellationToken ct) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var visit = await VisitService.LockAsync(db, visitId, ct);
            await access.EnsureResponsibleAsync(visitId, ct);
            var consult = await db.Consultations.SingleOrDefaultAsync(c => c.VisitId == visitId, ct) ?? throw AppException.NotFound();
            db.Entry(consult).Property(c => c.Version).OriginalValue = request.Version;
            if (consult.FinalizedAt is null)
            {
                throw new AppException(StatusCodes.Status409Conflict, "not_finalized", "این معاینه هنوز نهایی نشده است.");
            }

            if (visit.Status is VisitStatus.Completed or VisitStatus.Cancelled)
            {
                throw new AppException(StatusCodes.Status409Conflict, "visit_closed", "این مراجعه بسته شده است. برای تغییر، اصلاحیه ثبت کنید.");
            }

            db.ClinicalAmendments.Add(new ClinicalAmendment
            {
                Entity = nameof(Consultation),
                EntityId = consult.Id,
                Field = nameof(Consultation.FinalizedAt),
                OldValue = consult.FinalizedAt.Value.ToString("O"),
                Reason = request.Reason.Trim(),
                UserId = user.Id,
            });
            consult.FinalizedAt = null;
            if (visit.Status == VisitStatus.ReadyForDoctor)
            {
                MoveVisit(db, visit, VisitStatus.InConsultation, user.Id);
            }

            audit.Add("ConsultationReopened", "Visit", visitId.ToString());
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        }).RequirePermission("consultation.edit");

        consultations.MapPost("/{visitId:long}/amend", async (long visitId, AmendRequest request, AppDbContext db, ClinicalAccess access, CurrentUser user, AuditService audit, CancellationToken ct) =>
        {
            if (!AmendableFields.Contains(request.Field))
            {
                throw new AppException(StatusCodes.Status400BadRequest, "invalid_field", "این فیلد قابل اصلاح نیست.");
            }

            await access.EnsureResponsibleAsync(visitId, ct);
            var consult = await db.Consultations.SingleOrDefaultAsync(c => c.VisitId == visitId, ct) ?? throw AppException.NotFound();
            db.Entry(consult).Property(c => c.Version).OriginalValue = request.Version;
            if (consult.FinalizedAt is null)
            {
                throw new AppException(StatusCodes.Status409Conflict, "not_finalized", "این معاینه هنوز نهایی نشده و مستقیم قابل ویرایش است.");
            }

            var entry = db.Entry(consult).Property<string?>(request.Field);
            db.ClinicalAmendments.Add(new ClinicalAmendment
            {
                Entity = nameof(Consultation),
                EntityId = consult.Id,
                Field = request.Field,
                OldValue = entry.CurrentValue,
                NewValue = request.Value,
                Reason = request.Reason.Trim(),
                UserId = user.Id,
            });
            entry.CurrentValue = request.Value;
            audit.Add("ConsultationAmended", "Visit", visitId.ToString(), request.Field);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequirePermission("consultation.edit");

        var patients = app.MapGroup("/api/clinical/patients").WithTags("Clinical");

        patients.MapPost("/{patientId:long}/emergency-access", async (long patientId, EmergencyAccessRequest request, ClinicalAccess access, CancellationToken ct) =>
        {
            await access.GrantEmergencyAsync(patientId, request.Reason, ct);
            return Results.NoContent();
        }).RequirePermission("clinicalrecord.view");

        patients.MapGet("/{patientId:long}/history", async (long patientId, AppDbContext db, ClinicalAccess access, CancellationToken ct) =>
        {
            await access.EnsureCanViewAsync(patientId, ct);
            var rows = await db.Consultations.AsNoTracking()
                .Where(c => c.Visit!.PatientId == patientId)
                .OrderByDescending(c => c.Id)
                .Take(50)
                .Select(c => new { c.Id, c.VisitId, c.Visit!.VisitDate, DepartmentName = c.Visit.Department!.Name, DoctorName = c.Visit.Doctor != null ? c.Visit.Doctor.FullName : null, c.TreatmentPlan })
                .ToListAsync(ct);

            var ids = rows.Select(r => r.Id).ToList();
            var diagnoses = await db.Diagnoses.AsNoTracking().Where(d => ids.Contains(d.ConsultationId)).Select(d => new { d.ConsultationId, d.Eye, d.Text }).ToListAsync(ct);

            return Results.Ok(rows.Select(r => new HistoryItem(
                r.VisitId, r.VisitDate, r.DepartmentName, r.DoctorName, r.TreatmentPlan,
                diagnoses.Where(d => d.ConsultationId == r.Id).Select(d => new DiagnosisDto(d.Eye.ToString(), d.Text)).ToList())).ToList());
        }).RequirePermission("clinicalrecord.view");

        app.MapGet("/api/clinical/worklist", async (string kind, AppDbContext db, CurrentUser user, CancellationToken ct) =>
        {
            var today = KabulClock.Today;
            var query = db.Visits.AsNoTracking().Where(v => v.VisitDate == today);
            if (kind == "triage")
            {
                query = VisionQueue(query);
            }
            else if (kind == "doctor")
            {
                query = DoctorQueue(db, query, user.Id);
            }
            else
            {
                throw new AppException(StatusCodes.Status400BadRequest, "invalid_kind", "نوع فهرست معتبر نیست.");
            }

            var rows = await VisitService.QueryRows(db, query.OrderBy(v => v.QueueNumber).Take(200)).ToListAsync(ct);
            return Results.Ok(rows.Select(VisitService.ToRow).ToList());
        }).RequirePermission("visits.view").WithTags("Clinical");

        // Dates and patient names only, so the list is safe for a doctor's dashboard.
        app.MapGet("/api/follow-ups", async (AppDbContext db, CurrentUser user, CancellationToken ct) =>
        {
            var today = KabulClock.Today;
            var userId = user.Id;
            var rows = await db.FollowUps.AsNoTracking()
                .Where(f => f.CreatedByUserId == userId && f.DueDate >= today)
                .OrderBy(f => f.DueDate)
                .Take(50)
                .Select(f => new { f.Id, f.PatientId, f.DueDate, f.Note, f.Patient!.FirstName, f.Patient.LastName })
                .ToListAsync(ct);
            return Results.Ok(rows.Select(f => new { f.Id, f.PatientId, f.DueDate, f.Note, PatientName = $"{f.FirstName} {f.LastName}".Trim() }));
        }).RequirePermission("consultation.view").WithTags("Clinical");

        app.MapPost("/api/follow-ups", async (CreateFollowUpRequest request, AppDbContext db, ClinicalAccess access, CurrentUser user, AuditService audit, CancellationToken ct) =>
        {
            var patientId = await PatientOfVisitAsync(db, request.VisitId, ct);
            await access.EnsureResponsibleAsync(request.VisitId, ct);
            if (request.DueDate < KabulClock.Today)
            {
                throw new AppException(StatusCodes.Status400BadRequest, "invalid_date", "تاریخ پیگیری نمی‌تواند در گذشته باشد.");
            }

            var followUp = new FollowUp { PatientId = patientId, VisitId = request.VisitId, DueDate = request.DueDate, Note = Text.Clean(request.Note), CreatedByUserId = user.Id };
            db.FollowUps.Add(followUp);
            audit.Add("FollowUpCreated", "Visit", request.VisitId.ToString());
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/follow-ups/{followUp.Id}", new FollowUpDto(followUp.Id, followUp.DueDate, followUp.Note));
        }).RequirePermission("consultation.edit").WithTags("Clinical");
    }

    /// <summary>Everyone bound for a doctor passes through here, whichever department the desk chose.</summary>
    public static IQueryable<Visit> VisionQueue(IQueryable<Visit> visits) =>
        visits.Where(v => (v.Department!.Code == "triage" || v.Department!.Code == "doctor" || v.DoctorStaffId != null)
            && (v.Status == VisitStatus.Registered || v.Status == VisitStatus.Waiting || v.Status == VisitStatus.InPreExam));

    /// <summary>
    /// A patient belongs to the assigned doctor's account. Without one (nobody assigned, or a doctor with no login)
    /// the patient stays with whoever started the exam, and otherwise waits in the shared list so nobody is lost.
    /// A patient still waiting for Vision reaches the doctor only after it is complete.
    /// </summary>
    public static IQueryable<Visit> DoctorQueue(AppDbContext db, IQueryable<Visit> visits, long userId) =>
        visits.Where(v =>
            ((v.Doctor != null && v.Doctor.UserId != null && v.Doctor.UserId == userId)
                || ((v.Doctor == null || v.Doctor.UserId == null)
                    && (db.Consultations.Any(c => c.VisitId == v.Id && c.AuthorUserId == userId)
                        || (!db.Consultations.Any(c => c.VisitId == v.Id) && (v.Department!.Code == "doctor" || v.Status == VisitStatus.ReadyForDoctor))))) &&
            v.Status != VisitStatus.Completed && v.Status != VisitStatus.Cancelled && v.Status != VisitStatus.AwaitingPayment &&
            v.Status != VisitStatus.Registered && v.Status != VisitStatus.Waiting && v.Status != VisitStatus.InPreExam);

    private static void MoveVisit(AppDbContext db, Visit visit, VisitStatus to, long userId)
    {
        db.VisitStatusHistory.Add(new VisitStatusHistory { VisitId = visit.Id, FromStatus = visit.Status, ToStatus = to, ChangedByUserId = userId });
        visit.Status = to;
    }

    public static async Task<long> PatientOfVisitAsync(AppDbContext db, long visitId, CancellationToken ct)
    {
        var patientId = await db.Visits.Where(v => v.Id == visitId).Select(v => (long?)v.PatientId).SingleOrDefaultAsync(ct);
        return patientId ?? throw AppException.NotFound();
    }

    public static void EnsureNotCancelled(Visit visit)
    {
        if (visit.Status == VisitStatus.Cancelled)
        {
            throw new AppException(StatusCodes.Status409Conflict, "visit_closed", "این مراجعه لغو شده است.");
        }

        if (visit.Status == VisitStatus.AwaitingPayment)
        {
            throw new AppException(StatusCodes.Status409Conflict, "payment_required", "فیس معاینه این مراجعه هنوز پرداخت نشده است.");
        }
    }

    /// <summary>Vision is free and comes first for every patient; no hand-set status or direct exam goes around it.</summary>
    public static async Task EnsureVisionCompleteAsync(AppDbContext db, long visitId, CancellationToken ct)
    {
        if (!await db.PreExaminations.AnyAsync(p => p.VisitId == visitId && p.CompletedAt != null, ct))
        {
            throw new AppException(StatusCodes.Status409Conflict, "vision_required", "این مریض هنوز Vision را تکمیل نکرده است. ابتدا باید Vision ثبت و تکمیل شود.");
        }
    }

    /// <summary>A screen with no version opened before the first save, so it never saw what is stored now.</summary>
    private static DbUpdateConcurrencyException StaleScreen() => new();

    private static PreExamDto ToDto(PreExamination p) => new(
        p.Id, p.VisitId, p.ChiefComplaint, p.History, p.VisualAcuityOd, p.VisualAcuityOs, p.IopOd, p.IopOs, p.Measurements, p.Notes, p.RecordedAt, p.CompletedAt, p.Version);

    private static async Task<ConsultationDto> ToDtoAsync(AppDbContext db, Consultation c, bool canSeeConfidential, CancellationToken ct)
    {
        var diagnoses = await db.Diagnoses.AsNoTracking().Where(d => d.ConsultationId == c.Id).OrderBy(d => d.Id).Select(d => new DiagnosisDto(d.Eye.ToString(), d.Text)).ToListAsync(ct);
        return new ConsultationDto(c.Id, c.VisitId, c.FindingsOd, c.FindingsOs, c.TreatmentPlan, canSeeConfidential ? c.ConfidentialNote : null, diagnoses, c.FinalizedAt, c.Version);
    }
}
