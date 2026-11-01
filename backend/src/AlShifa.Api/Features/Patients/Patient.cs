using System.ComponentModel.DataAnnotations;

namespace AlShifa.Api.Features.Patients;

public enum Gender
{
    Male,
    Female,
    Other,
}

public class Patient
{
    public long Id { get; set; }

    [MaxLength(20)]
    public string FileNumber { get; set; } = "";

    [MaxLength(100)]
    public string FirstName { get; set; } = "";

    [MaxLength(100)]
    public string? LastName { get; set; }

    [MaxLength(100)]
    public string? FatherName { get; set; }

    public Gender Gender { get; set; }

    public DateOnly? BirthDate { get; set; }

    public int? EstimatedBirthYear { get; set; }

    [MaxLength(30)]
    public string? Phone { get; set; }

    [MaxLength(300)]
    public string? Address { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public long CreatedByUserId { get; set; }

    public int Version { get; set; }
}

public class PatientChangeLog
{
    public long Id { get; set; }

    public long PatientId { get; set; }

    public Patient? Patient { get; set; }

    [MaxLength(50)]
    public string Field { get; set; } = "";

    [MaxLength(300)]
    public string? OldValue { get; set; }

    [MaxLength(300)]
    public string? NewValue { get; set; }

    public long ChangedByUserId { get; set; }

    public DateTime ChangedAt { get; set; } = DateTime.UtcNow;
}
