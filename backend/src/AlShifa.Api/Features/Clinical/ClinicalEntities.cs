using System.ComponentModel.DataAnnotations;
using AlShifa.Api.Features.Patients;
using AlShifa.Api.Features.Visits;

namespace AlShifa.Api.Features.Clinical;

// Clinical fields are provisional until confirmed by the medical lead (WORKFLOWS.md M-1 to M-6).
// Values are stored exactly as entered; the system never derives or suggests medical values.

public class PreExamination
{
    public long Id { get; set; }

    public long VisitId { get; set; }

    public Visit? Visit { get; set; }

    [MaxLength(500)]
    public string? ChiefComplaint { get; set; }

    [MaxLength(1000)]
    public string? History { get; set; }

    [MaxLength(30)]
    public string? VisualAcuityOd { get; set; }

    [MaxLength(30)]
    public string? VisualAcuityOs { get; set; }

    [MaxLength(30)]
    public string? IopOd { get; set; }

    [MaxLength(30)]
    public string? IopOs { get; set; }

    [MaxLength(1000)]
    public string? Measurements { get; set; }

    [MaxLength(2000)]
    public string? Notes { get; set; }

    public long RecordedByUserId { get; set; }

    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;

    public DateTime? CompletedAt { get; set; }

    public int Version { get; set; }
}

public class Consultation
{
    public long Id { get; set; }

    public long VisitId { get; set; }

    public Visit? Visit { get; set; }

    public long AuthorUserId { get; set; }

    [MaxLength(4000)]
    public string? FindingsOd { get; set; }

    [MaxLength(4000)]
    public string? FindingsOs { get; set; }

    [MaxLength(4000)]
    public string? TreatmentPlan { get; set; }

    [MaxLength(4000)]
    public string? ConfidentialNote { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? FinalizedAt { get; set; }

    public int Version { get; set; }
}

public enum EyeSide
{
    None,
    OD,
    OS,
    Both,
}

public class Diagnosis
{
    public long Id { get; set; }

    public long ConsultationId { get; set; }

    public Consultation? Consultation { get; set; }

    public EyeSide Eye { get; set; }

    [MaxLength(500)]
    public string Text { get; set; } = "";
}

public class ClinicalAmendment
{
    public long Id { get; set; }

    [MaxLength(40)]
    public string Entity { get; set; } = "";

    public long EntityId { get; set; }

    [MaxLength(40)]
    public string Field { get; set; } = "";

    [MaxLength(4000)]
    public string? OldValue { get; set; }

    [MaxLength(4000)]
    public string? NewValue { get; set; }

    [MaxLength(500)]
    public string Reason { get; set; } = "";

    public long UserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public enum PrescriptionStatus
{
    Issued,
    PartiallyDispensed,
    Dispensed,
    Cancelled,
}

public class MedicalPrescription
{
    public long Id { get; set; }

    [MaxLength(20)]
    public string RxNumber { get; set; } = "";

    public long PatientId { get; set; }

    public Patient? Patient { get; set; }

    public long VisitId { get; set; }

    public Visit? Visit { get; set; }

    public long DoctorUserId { get; set; }

    [MaxLength(1000)]
    public string? Notes { get; set; }

    public PrescriptionStatus Status { get; set; } = PrescriptionStatus.Issued;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public int Version { get; set; }

    public List<MedicalPrescriptionItem> Items { get; set; } = [];
}

public class MedicalPrescriptionItem
{
    public long Id { get; set; }

    public long PrescriptionId { get; set; }

    [MaxLength(200)]
    public string DrugName { get; set; } = "";

    [MaxLength(100)]
    public string? Dose { get; set; }

    [MaxLength(100)]
    public string? Route { get; set; }

    [MaxLength(100)]
    public string? Duration { get; set; }

    [MaxLength(300)]
    public string? Instructions { get; set; }

    public decimal Quantity { get; set; }

    public decimal DispensedQuantity { get; set; }
}

public class OpticalPrescription
{
    public long Id { get; set; }

    [MaxLength(20)]
    public string RxNumber { get; set; } = "";

    public long PatientId { get; set; }

    public Patient? Patient { get; set; }

    public long VisitId { get; set; }

    public Visit? Visit { get; set; }

    public long DoctorUserId { get; set; }

    [MaxLength(10)] public string? SphereOd { get; set; }
    [MaxLength(10)] public string? CylinderOd { get; set; }
    [MaxLength(10)] public string? AxisOd { get; set; }
    [MaxLength(10)] public string? AddOd { get; set; }
    [MaxLength(10)] public string? SphereOs { get; set; }
    [MaxLength(10)] public string? CylinderOs { get; set; }
    [MaxLength(10)] public string? AxisOs { get; set; }
    [MaxLength(10)] public string? AddOs { get; set; }
    [MaxLength(10)] public string? Pd { get; set; }

    [MaxLength(1000)]
    public string? Notes { get; set; }

    public bool IsCancelled { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public int Version { get; set; }
}

public class FollowUp
{
    public long Id { get; set; }

    public long PatientId { get; set; }

    public Patient? Patient { get; set; }

    public long VisitId { get; set; }

    public DateOnly DueDate { get; set; }

    [MaxLength(300)]
    public string? Note { get; set; }

    public long CreatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class ClinicalAccessGrant
{
    public long Id { get; set; }

    public long UserId { get; set; }

    public long PatientId { get; set; }

    [MaxLength(500)]
    public string Reason { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime ExpiresAt { get; set; }
}

public class DiagnosticTest
{
    public int Id { get; set; }

    [MaxLength(150)]
    public string Name { get; set; } = "";

    public bool IsActive { get; set; } = true;

    /// <summary>Billable service charged when this test is ordered; null if the test is not billed.</summary>
    public int? ServiceId { get; set; }

    public Finance.Service? Service { get; set; }
}

public enum DiagnosticStatus
{
    Requested,
    InProgress,
    ResultReady,
    Cancelled,
}

public class DiagnosticOrder
{
    public long Id { get; set; }

    [MaxLength(20)]
    public string OrderNumber { get; set; } = "";

    public long PatientId { get; set; }

    public Patient? Patient { get; set; }

    public long? VisitId { get; set; }

    public Visit? Visit { get; set; }

    public int TestId { get; set; }

    public DiagnosticTest? Test { get; set; }

    public long RequestedByUserId { get; set; }

    [MaxLength(500)]
    public string? Note { get; set; }

    public DiagnosticStatus Status { get; set; } = DiagnosticStatus.Requested;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public int Version { get; set; }

    public DiagnosticResult? Result { get; set; }
}

public class DiagnosticResult
{
    public long Id { get; set; }

    public long OrderId { get; set; }

    public DiagnosticOrder? Order { get; set; }

    [MaxLength(4000)]
    public string ResultText { get; set; } = "";

    public long PerformedByUserId { get; set; }

    public DateTime PerformedAt { get; set; } = DateTime.UtcNow;
}

public enum SurgeryStatus
{
    Proposed,
    Scheduled,
    InProgress,
    Completed,
    FollowUp,
    Closed,
    Cancelled,
}

public class SurgeryCase
{
    public long Id { get; set; }

    [MaxLength(20)]
    public string CaseNumber { get; set; } = "";

    public long PatientId { get; set; }

    public Patient? Patient { get; set; }

    public long VisitId { get; set; }

    public Visit? Visit { get; set; }

    [MaxLength(200)]
    public string SurgeryType { get; set; } = "";

    public EyeSide Eye { get; set; }

    public long ResponsibleUserId { get; set; }

    public DateTime? ScheduledAt { get; set; }

    public SurgeryStatus Status { get; set; } = SurgeryStatus.Proposed;

    [MaxLength(2000)]
    public string? PreOpChecks { get; set; }

    public bool ConsentRecorded { get; set; }

    [MaxLength(4000)]
    public string? OperationReport { get; set; }

    [MaxLength(2000)]
    public string? PostOpStatus { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public int Version { get; set; }
}
