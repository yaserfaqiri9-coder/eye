using System.ComponentModel.DataAnnotations;
using AlShifa.Api.Features.Patients;
using AlShifa.Api.Features.Users;

namespace AlShifa.Api.Features.Visits;

public enum VisitStatus
{
    Registered,
    Waiting,
    InPreExam,
    ReadyForDoctor,
    InConsultation,
    AwaitingDiagnostic,
    FollowUpRequired,
    Completed,
    Cancelled,
}

public class Visit
{
    public long Id { get; set; }

    [MaxLength(20)]
    public string VisitNumber { get; set; } = "";

    public long PatientId { get; set; }

    public Patient? Patient { get; set; }

    public int DepartmentId { get; set; }

    public Department? Department { get; set; }

    public long? DoctorStaffId { get; set; }

    public Staff? Doctor { get; set; }

    public VisitStatus Status { get; set; } = VisitStatus.Registered;

    [MaxLength(300)]
    public string? Reason { get; set; }

    /// <summary>Calendar day in Asia/Kabul; the queue number restarts on each day.</summary>
    public DateOnly VisitDate { get; set; }

    public int QueueNumber { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public long CreatedByUserId { get; set; }

    public int Version { get; set; }
}

/// <summary>
/// The paid queue number reception issues before a patient's details are taken. It holds the visit fee's invoice
/// and becomes a Visit once, so no placeholder patient is ever needed.
/// </summary>
public class VisitToken
{
    public long Id { get; set; }

    public int QueueNumber { get; set; }

    public DateOnly TokenDate { get; set; }

    public int DepartmentId { get; set; }

    public Department? Department { get; set; }

    public long? DoctorStaffId { get; set; }

    public Staff? Doctor { get; set; }

    public long InvoiceId { get; set; }

    public Finance.Invoice? Invoice { get; set; }

    public long? VisitId { get; set; }

    public Visit? Visit { get; set; }

    public long CreatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public int Version { get; set; }
}

public class VisitStatusHistory
{
    public long Id { get; set; }

    public long VisitId { get; set; }

    public Visit? Visit { get; set; }

    public VisitStatus? FromStatus { get; set; }

    public VisitStatus ToStatus { get; set; }

    public long ChangedByUserId { get; set; }

    public DateTime ChangedAt { get; set; } = DateTime.UtcNow;
}

public enum AppointmentStatus
{
    Scheduled,
    CheckedIn,
    Cancelled,
    NoShow,
}

public class Appointment
{
    public long Id { get; set; }

    public long PatientId { get; set; }

    public Patient? Patient { get; set; }

    public long DoctorStaffId { get; set; }

    public Staff? Doctor { get; set; }

    public DateTime ScheduledAt { get; set; }

    public AppointmentStatus Status { get; set; } = AppointmentStatus.Scheduled;

    [MaxLength(300)]
    public string? Note { get; set; }

    public long? VisitId { get; set; }

    public Visit? Visit { get; set; }

    public long CreatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public int Version { get; set; }
}
