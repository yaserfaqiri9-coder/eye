using System.ComponentModel.DataAnnotations;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AlShifa.Api.Features.Patients;

public sealed record PatientListItem(long Id, string FileNumber, string FirstName, string? LastName, string? FatherName, string Gender, int? AgeYears, string? Phone);

public sealed record PatientDto(
    long Id, string FileNumber, string FirstName, string? LastName, string? FatherName, string Gender,
    DateOnly? BirthDate, int? EstimatedBirthYear, int? AgeYears, string? Phone, string? Address, DateTime CreatedAt, int Version);

public sealed record SavePatientRequest(
    [Required, MaxLength(100)] string FirstName,
    [MaxLength(100)] string? LastName,
    [MaxLength(100)] string? FatherName,
    Gender Gender,
    DateOnly? BirthDate,
    [Range(0, 130)] int? EstimatedAge,
    [MaxLength(30)] string? Phone,
    [MaxLength(300)] string? Address,
    bool ConfirmNotDuplicate = false,
    int? Version = null);

public sealed record PatientChangeDto(string Field, string? OldValue, string? NewValue, DateTime ChangedAt, string? ChangedBy);

public static class PatientEndpoints
{
    private static readonly string[] EventGroups = [Groups.Reception, Groups.Triage];

    public static void MapPatients(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/patients").WithTags("Patients");

        group.MapGet("/", async (AppDbContext db, string? q, int page = 1, int pageSize = 25, CancellationToken ct = default) =>
        {
            pageSize = Math.Clamp(pageSize, 1, 100);
            page = Math.Max(page, 1);

            var query = db.Patients.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim();
                query = query.Where(p =>
                    p.FileNumber.StartsWith(term) ||
                    p.FirstName.StartsWith(term) ||
                    (p.LastName != null && p.LastName.StartsWith(term)) ||
                    (p.Phone != null && p.Phone.StartsWith(term)));
            }

            var total = await query.CountAsync(ct);
            var rows = await query
                .OrderByDescending(p => p.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(p => new { p.Id, p.FileNumber, p.FirstName, p.LastName, p.FatherName, p.Gender, p.BirthDate, p.EstimatedBirthYear, p.Phone })
                .ToListAsync(ct);

            var items = rows.Select(p => new PatientListItem(
                p.Id, p.FileNumber, p.FirstName, p.LastName, p.FatherName, p.Gender.ToString(),
                AgeOf(p.BirthDate, p.EstimatedBirthYear), p.Phone)).ToList();

            return Results.Ok(new PagedResult<PatientListItem>(items, total, page, pageSize));
        }).RequirePermission("patients.view");

        group.MapGet("/{id:long}", async (long id, AppDbContext db, CancellationToken ct) =>
        {
            var patient = await db.Patients.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, ct) ?? throw AppException.NotFound();
            return Results.Ok(ToDto(patient));
        }).RequirePermission("patients.view");

        group.MapGet("/{id:long}/changes", async (long id, AppDbContext db, CancellationToken ct) =>
        {
            var changes = await (
                from c in db.PatientChangeLogs.AsNoTracking()
                join u in db.Users on c.ChangedByUserId equals u.Id
                where c.PatientId == id
                orderby c.Id descending
                select new PatientChangeDto(c.Field, c.OldValue, c.NewValue, c.ChangedAt, u.FullName)).Take(100).ToListAsync(ct);
            return Results.Ok(changes);
        }).RequirePermission("patients.edit");

        group.MapPost("/", async (SavePatientRequest request, AppDbContext db, Idempotency idempotency, NumberService numbers, CurrentUser user, AuditService audit, Outbox outbox, CancellationToken ct) =>
            await idempotency.RunAsync(request, async () =>
            {
                if (!request.ConfirmNotDuplicate)
                {
                    var matches = await FindPossibleDuplicatesAsync(db, request, ct);
                    if (matches.Count > 0)
                    {
                        throw new PossibleDuplicateException(matches);
                    }
                }

                var patient = new Patient { CreatedByUserId = user.Id };
                Apply(patient, request);
                patient.FileNumber = $"P-{await numbers.NextAsync("patient", ct: ct):D6}";
                db.Patients.Add(patient);
                await db.SaveChangesAsync(ct);

                audit.Add("PatientCreated", "Patient", patient.Id.ToString());
                outbox.Add("PatientRegistered", new { patientId = patient.Id }, EventGroups);
                await db.SaveChangesAsync(ct);
                return (StatusCodes.Status201Created, ToDto(patient));
            }, ct)).RequirePermission("patients.create");

        group.MapPut("/{id:long}", async (long id, SavePatientRequest request, AppDbContext db, CurrentUser user, AuditService audit, CancellationToken ct) =>
        {
            if (request.Version is null)
            {
                throw new AppException(StatusCodes.Status400BadRequest, "version_required", "نسخه رکورد ارسال نشده است.");
            }

            await using var tx = await db.Database.BeginTransactionAsync(ct);

            // The change log's foreign key takes a shared lock on this row. Without locking it first, two editors deadlock
            // and the loser hears "busy" instead of being told that the record changed.
            var patient = await db.Patients.FromSqlInterpolated($"SELECT * FROM Patients WHERE Id = {id} FOR UPDATE").SingleOrDefaultAsync(ct) ?? throw AppException.NotFound();
            db.Entry(patient).Property(p => p.Version).OriginalValue = request.Version.Value;

            var before = Snapshot(patient);
            Apply(patient, request);
            foreach (var (field, oldValue) in before)
            {
                var newValue = Snapshot(patient)[field];
                if (oldValue != newValue)
                {
                    db.PatientChangeLogs.Add(new PatientChangeLog { PatientId = id, Field = field, OldValue = oldValue, NewValue = newValue, ChangedByUserId = user.Id });
                }
            }

            audit.Add("PatientEdited", "Patient", id.ToString());
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Ok(ToDto(patient));
        }).RequirePermission("patients.edit");
    }

    private static async Task<List<PatientListItem>> FindPossibleDuplicatesAsync(AppDbContext db, SavePatientRequest r, CancellationToken ct)
    {
        var first = r.FirstName.Trim();
        var phone = string.IsNullOrWhiteSpace(r.Phone) ? null : r.Phone.Trim();
        var father = string.IsNullOrWhiteSpace(r.FatherName) ? null : r.FatherName.Trim();

        var rows = await db.Patients.AsNoTracking()
            .Where(p => (phone != null && p.Phone == phone && p.FirstName == first) || (father != null && p.FirstName == first && p.FatherName == father))
            .Take(5)
            .Select(p => new { p.Id, p.FileNumber, p.FirstName, p.LastName, p.FatherName, p.Gender, p.BirthDate, p.EstimatedBirthYear, p.Phone })
            .ToListAsync(ct);

        return rows.Select(p => new PatientListItem(p.Id, p.FileNumber, p.FirstName, p.LastName, p.FatherName, p.Gender.ToString(), AgeOf(p.BirthDate, p.EstimatedBirthYear), p.Phone)).ToList();
    }

    private static void Apply(Patient patient, SavePatientRequest r)
    {
        if (r.BirthDate is { } birth && birth > KabulClock.Today)
        {
            throw new AppException(StatusCodes.Status400BadRequest, "invalid_birth_date", "تاریخ تولد نمی‌تواند در آینده باشد.");
        }

        patient.FirstName = r.FirstName.Trim();
        patient.LastName = Clean(r.LastName);
        patient.FatherName = Clean(r.FatherName);
        patient.Gender = r.Gender;
        patient.BirthDate = r.BirthDate;
        patient.EstimatedBirthYear = r.BirthDate is null && r.EstimatedAge is { } age ? KabulClock.Today.Year - age : r.BirthDate is null ? patient.EstimatedBirthYear : null;
        patient.Phone = Clean(r.Phone);
        patient.Address = Clean(r.Address);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Dictionary<string, string?> Snapshot(Patient p) => new()
    {
        ["FirstName"] = p.FirstName,
        ["LastName"] = p.LastName,
        ["FatherName"] = p.FatherName,
        ["Gender"] = p.Gender.ToString(),
        ["BirthDate"] = p.BirthDate?.ToString("yyyy-MM-dd"),
        ["EstimatedBirthYear"] = p.EstimatedBirthYear?.ToString(),
        ["Phone"] = p.Phone,
        ["Address"] = p.Address,
    };

    internal static int? AgeOf(DateOnly? birth, int? estimatedYear)
    {
        var today = KabulClock.Today;
        if (birth is { } b)
        {
            var age = today.Year - b.Year;
            return b.AddYears(age) > today ? age - 1 : age;
        }

        return estimatedYear is { } year ? today.Year - year : null;
    }

    private static PatientDto ToDto(Patient p) => new(
        p.Id, p.FileNumber, p.FirstName, p.LastName, p.FatherName, p.Gender.ToString(), p.BirthDate, p.EstimatedBirthYear,
        AgeOf(p.BirthDate, p.EstimatedBirthYear), p.Phone, p.Address, p.CreatedAt, p.Version);
}

public sealed class PossibleDuplicateException(IReadOnlyList<PatientListItem> matches)
    : Exception("possible_duplicate")
{
    public IReadOnlyList<PatientListItem> Matches { get; } = matches;
}
