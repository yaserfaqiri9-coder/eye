using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AlShifa.Api.Features.Settings;

public class Attachment
{
    public long Id { get; set; }

    [MaxLength(30)]
    public string OwnerType { get; set; } = "";

    public long OwnerId { get; set; }

    [MaxLength(200)]
    public string FileName { get; set; } = "";

    /// <summary>Random server-side name; nothing from the upload ever reaches the file system path.</summary>
    [MaxLength(64)]
    public string StoredName { get; set; } = "";

    [MaxLength(50)]
    public string ContentType { get; set; } = "";

    public long Size { get; set; }

    [MaxLength(64)]
    public string Sha256 { get; set; } = "";

    public long UploadedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed record SettingsDto(string? HospitalName, string? Address, string? Phone, string? PrintFooter, long? LogoAttachmentId);

public sealed record SaveSettingsRequest(
    [MaxLength(150)] string? HospitalName,
    [MaxLength(300)] string? Address,
    [MaxLength(60)] string? Phone,
    [MaxLength(300)] string? PrintFooter);

public sealed record AttachmentRow(long Id, string FileName, string ContentType, long Size, DateTime CreatedAt);

public sealed record SaveRegisterRequest([Required, MaxLength(100)] string Name);

public static class SettingsEndpoints
{
    private const long MaxFileSize = 10 * 1024 * 1024;

    private const string HospitalNameKey = "hospital.name";
    private const string AddressKey = "hospital.address";
    private const string PhoneKey = "hospital.phone";
    private const string FooterKey = "print.footer";

    /// <summary>Owner type decides who may read and write. Anything else is rejected.</summary>
    private static readonly Dictionary<string, (string Read, string Write)> Owners = new()
    {
        ["DiagnosticOrder"] = ("diagnostics.view", "diagnostics.edit"),
        ["SurgeryCase"] = ("surgery.edit", "surgery.edit"),
        ["Logo"] = ("", "settings.edit"),
    };

    public static void MapSettings(this IEndpointRouteBuilder app)
    {
        var settings = app.MapGroup("/api/settings").WithTags("Settings");

        settings.MapGet("/", async (AppDbContext db, CancellationToken ct) => Results.Ok(await LoadAsync(db, ct))).RequireAuthorization();

        settings.MapPut("/", async (SaveSettingsRequest request, AppDbContext db, AuditService audit, CancellationToken ct) =>
        {
            await UpsertAsync(db, HospitalNameKey, request.HospitalName, ct);
            await UpsertAsync(db, AddressKey, request.Address, ct);
            await UpsertAsync(db, PhoneKey, request.Phone, ct);
            await UpsertAsync(db, FooterKey, request.PrintFooter, ct);
            audit.Add("SettingsChanged", "Setting", null);
            await db.SaveChangesAsync(ct);
            return Results.Ok(await LoadAsync(db, ct));
        }).RequirePermission("settings.edit");

        settings.MapGet("/backup-status", (IConfiguration config) => Results.Ok(BackupStatus.Read(config["Backup:StatusFile"], DateTime.UtcNow)))
            .RequirePermission("settings.view");

        app.MapPost("/api/cash/registers", async (SaveRegisterRequest request, AppDbContext db, AuditService audit, CancellationToken ct) =>
        {
            var register = new Finance.CashRegister { Name = request.Name.Trim() };
            db.CashRegisters.Add(register);
            audit.Add("CashRegisterCreated", "CashRegister", null, register.Name);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/cash/registers/{register.Id}", new { register.Id });
        }).RequirePermission("settings.edit").WithTags("Settings");

        var attachments = app.MapGroup("/api/attachments").WithTags("Attachments");

        attachments.MapGet("/", async (string ownerType, long ownerId, AppDbContext db, HttpContext http, CancellationToken ct) =>
        {
            EnsureAllowed(http, ownerType, write: false);
            return Results.Ok(await db.Attachments.AsNoTracking()
                .Where(a => a.OwnerType == ownerType && a.OwnerId == ownerId)
                .OrderByDescending(a => a.Id)
                .Select(a => new AttachmentRow(a.Id, a.FileName, a.ContentType, a.Size, a.CreatedAt))
                .ToListAsync(ct));
        }).RequireAuthorization();

        // Download goes through the API so the same permission check applies to every read.
        attachments.MapGet("/{id:long}", async (long id, AppDbContext db, HttpContext http, IConfiguration config, IHostEnvironment env, AuditService audit, CancellationToken ct) =>
        {
            var attachment = await db.Attachments.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, ct) ?? throw AppException.NotFound();
            EnsureAllowed(http, attachment.OwnerType, write: false);

            var path = Path.Combine(StorageRoot(config, env), attachment.StoredName);
            if (!File.Exists(path))
            {
                throw AppException.NotFound();
            }

            if (attachment.OwnerType != "Logo")
            {
                audit.Add("AttachmentDownloaded", "Attachment", id.ToString());
                await db.SaveChangesAsync(ct);
            }

            http.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.File(path, attachment.ContentType, attachment.OwnerType == "Logo" ? null : attachment.FileName);
        }).RequireAuthorization();

        // Protected by the CSRF middleware in Program.cs, which validates the header before this runs.
        attachments.MapPost("/", async (HttpContext http, AppDbContext db, CurrentUser user, AuditService audit, IConfiguration config, IHostEnvironment env, CancellationToken ct) =>
        {
            var form = await http.Request.ReadFormAsync(ct);
            var ownerType = form["ownerType"].ToString();
            if (!long.TryParse(form["ownerId"], out var ownerId))
            {
                throw new AppException(StatusCodes.Status400BadRequest, "invalid_owner", "مالک فایل معتبر نیست.");
            }

            EnsureAllowed(http, ownerType, write: true);
            await EnsureOwnerExistsAsync(db, ownerType, ownerId, ct);

            var file = form.Files["file"];
            if (file is null || file.Length == 0)
            {
                throw new AppException(StatusCodes.Status400BadRequest, "file_required", "فایلی انتخاب نشده است.");
            }

            if (file.Length > MaxFileSize)
            {
                throw new AppException(StatusCodes.Status400BadRequest, "file_too_large", "حجم فایل بیشتر از ۱۰ مگابایت است.");
            }

            await using var buffer = new MemoryStream();
            await file.CopyToAsync(buffer, ct);
            var bytes = buffer.ToArray();

            var contentType = DetectType(bytes)
                ?? throw new AppException(StatusCodes.Status400BadRequest, "file_type_not_allowed", "فقط فایل PDF، PNG یا JPEG مجاز است.");
            if (ownerType == "Logo" && contentType == "application/pdf")
            {
                throw new AppException(StatusCodes.Status400BadRequest, "file_type_not_allowed", "لوگو باید تصویر باشد.");
            }

            var storedName = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            var root = StorageRoot(config, env);
            Directory.CreateDirectory(root);
            await File.WriteAllBytesAsync(Path.Combine(root, storedName), bytes, ct);

            var attachment = new Attachment
            {
                OwnerType = ownerType,
                OwnerId = ownerId,
                FileName = Path.GetFileName(file.FileName)[..Math.Min(Path.GetFileName(file.FileName).Length, 200)],
                StoredName = storedName,
                ContentType = contentType,
                Size = bytes.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
                UploadedByUserId = user.Id,
            };
            db.Attachments.Add(attachment);
            audit.Add("AttachmentUploaded", ownerType, ownerId.ToString(), attachment.FileName);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/attachments/{attachment.Id}", new AttachmentRow(attachment.Id, attachment.FileName, attachment.ContentType, attachment.Size, attachment.CreatedAt));
        }).RequireAuthorization().DisableAntiforgery();
    }

    public static async Task<SettingsDto> LoadAsync(AppDbContext db, CancellationToken ct)
    {
        var values = await db.Settings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        var logo = await db.Attachments.AsNoTracking().Where(a => a.OwnerType == "Logo").OrderByDescending(a => a.Id).Select(a => (long?)a.Id).FirstOrDefaultAsync(ct);
        return new SettingsDto(values.GetValueOrDefault(HospitalNameKey), values.GetValueOrDefault(AddressKey), values.GetValueOrDefault(PhoneKey), values.GetValueOrDefault(FooterKey), logo);
    }

    private static async Task UpsertAsync(AppDbContext db, string key, string? value, CancellationToken ct)
    {
        var setting = await db.Settings.SingleOrDefaultAsync(s => s.Key == key, ct);
        if (setting is null)
        {
            db.Settings.Add(new Setting { Key = key, Value = value?.Trim() ?? "" });
        }
        else
        {
            setting.Value = value?.Trim() ?? "";
        }
    }

    private static void EnsureAllowed(HttpContext http, string ownerType, bool write)
    {
        if (!Owners.TryGetValue(ownerType, out var rule))
        {
            throw new AppException(StatusCodes.Status400BadRequest, "invalid_owner", "مالک فایل معتبر نیست.");
        }

        var needed = write ? rule.Write : rule.Read;
        if (needed.Length > 0 && !http.User.HasClaim(RolePermissions.ClaimType, needed))
        {
            throw new AppException(StatusCodes.Status403Forbidden, "forbidden", "شما اجازه این عملیات را ندارید.");
        }
    }

    private static async Task EnsureOwnerExistsAsync(AppDbContext db, string ownerType, long ownerId, CancellationToken ct)
    {
        var exists = ownerType switch
        {
            "DiagnosticOrder" => await db.DiagnosticOrders.AnyAsync(o => o.Id == ownerId, ct),
            "SurgeryCase" => await db.SurgeryCases.AnyAsync(c => c.Id == ownerId, ct),
            _ => true,
        };
        if (!exists)
        {
            throw AppException.NotFound();
        }
    }

    private static string StorageRoot(IConfiguration config, IHostEnvironment env) =>
        config["Storage:Path"] ?? Path.Combine(env.ContentRootPath, "storage");

    /// <summary>Judges the file by its first bytes; the claimed name and content type are not trusted.</summary>
    private static string? DetectType(byte[] b)
    {
        if (b.Length >= 4 && b[0] == 0x25 && b[1] == 0x50 && b[2] == 0x44 && b[3] == 0x46)
        {
            return "application/pdf";
        }

        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A)
        {
            return "image/png";
        }

        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF)
        {
            return "image/jpeg";
        }

        return null;
    }
}
