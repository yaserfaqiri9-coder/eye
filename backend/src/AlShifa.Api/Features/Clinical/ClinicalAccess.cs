using System.Security.Claims;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Visits;
using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AlShifa.Api.Features.Clinical;

/// <summary>
/// Record-level check on top of permissions. A user may open a clinical record only with a care
/// relationship: assigned doctor on any visit, a currently open visit, or an audited emergency grant.
/// </summary>
public sealed class ClinicalAccess(AppDbContext db, CurrentUser user, AuditService audit, IHttpContextAccessor accessor)
{
    public static readonly TimeSpan GrantDuration = TimeSpan.FromHours(4);

    public async Task EnsureCanViewAsync(long patientId, CancellationToken ct)
    {
        var principal = accessor.HttpContext!.User;
        if (!principal.HasClaim(RolePermissions.ClaimType, "clinicalrecord.view"))
        {
            throw new AppException(StatusCodes.Status403Forbidden, "forbidden", "شما اجازه مشاهده پرونده طبی را ندارید.");
        }

        if (!await HasCareRelationshipAsync(patientId, ct))
        {
            throw new AppException(StatusCodes.Status403Forbidden, "clinical_access_required", "این مریض به شما مربوط نیست. برای دسترسی اضطراری دلیل ثبت کنید.");
        }

        audit.Add("ClinicalRecordViewed", "Patient", patientId.ToString());
        await db.SaveChangesAsync(ct);
    }

    private async Task<bool> HasCareRelationshipAsync(long patientId, CancellationToken ct)
    {
        var userId = user.Id;
        var today = KabulClock.Today;

        var openVisit = await db.Visits.AnyAsync(v =>
            v.PatientId == patientId && v.VisitDate == today &&
            v.Status != VisitStatus.Completed && v.Status != VisitStatus.Cancelled, ct);
        if (openVisit)
        {
            return true;
        }

        var assigned = await db.Visits.AnyAsync(v => v.PatientId == patientId && v.Doctor != null && v.Doctor.UserId == userId, ct);
        if (assigned)
        {
            return true;
        }

        var authored = await db.Consultations.AnyAsync(c => c.AuthorUserId == userId && c.Visit!.PatientId == patientId, ct);
        if (authored)
        {
            return true;
        }

        var now = DateTime.UtcNow;
        return await db.ClinicalAccessGrants.AnyAsync(g => g.UserId == userId && g.PatientId == patientId && g.ExpiresAt > now, ct);
    }

    /// <summary>The account answering for a visit: the assigned doctor's, otherwise whoever started the exam. Null until a doctor takes it.</summary>
    public async Task<long?> ResponsibleUserAsync(long visitId, CancellationToken ct)
    {
        var visit = await db.Visits.Where(v => v.Id == visitId).Select(v => new { AssignedUserId = v.Doctor != null ? v.Doctor.UserId : null }).SingleOrDefaultAsync(ct)
            ?? throw AppException.NotFound();
        return visit.AssignedUserId ?? await db.Consultations.Where(c => c.VisitId == visitId).Select(c => (long?)c.AuthorUserId).SingleOrDefaultAsync(ct);
    }

    /// <summary>
    /// A doctor changes only their own patient's record; a patient no doctor has taken yet is free, as in the worklist.
    /// Other roles stay limited by their permissions, and emergency access opens a record for reading only.
    /// </summary>
    public async Task EnsureResponsibleAsync(long visitId, CancellationToken ct, long? alsoAllowedUserId = null)
    {
        if (!accessor.HttpContext!.User.HasClaim(RolePermissions.ClaimType, "consultation.edit"))
        {
            return;
        }

        var responsible = await ResponsibleUserAsync(visitId, ct);
        if (responsible is not null && responsible != user.Id && alsoAllowedUserId != user.Id)
        {
            throw new AppException(StatusCodes.Status403Forbidden, "not_responsible_doctor", "این مریض به داکتر دیگری سپرده شده است. برای تغییر پرونده، ابتدا مسئولیت باید انتقال داده شود.");
        }
    }

    public async Task GrantEmergencyAsync(long patientId, string reason, CancellationToken ct)
    {
        if (!accessor.HttpContext!.User.HasClaim(RolePermissions.ClaimType, "clinicalrecord.view"))
        {
            throw new AppException(StatusCodes.Status403Forbidden, "forbidden", "شما اجازه مشاهده پرونده طبی را ندارید.");
        }

        if (!await db.Patients.AnyAsync(p => p.Id == patientId, ct))
        {
            throw AppException.NotFound();
        }

        db.ClinicalAccessGrants.Add(new ClinicalAccessGrant
        {
            UserId = user.Id,
            PatientId = patientId,
            Reason = reason.Trim(),
            ExpiresAt = DateTime.UtcNow.Add(GrantDuration),
        });
        audit.Add("ClinicalEmergencyAccess", "Patient", patientId.ToString(), reason.Trim());
        await db.SaveChangesAsync(ct);
    }
}
