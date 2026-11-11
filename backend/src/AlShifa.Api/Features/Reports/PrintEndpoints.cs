using System.Globalization;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Clinical;
using AlShifa.Api.Features.Finance;
using AlShifa.Api.Features.Inventory;
using AlShifa.Api.Features.Patients;
using AlShifa.Api.Features.Settings;
using AlShifa.Api.Features.Visits;
using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AlShifa.Api.Features.Reports;

public sealed record PrintField(string Label, string? Value);

/// <param name="Totals">The first entry is the amount band at the foot of the paper; any others print as plain rows under it.</param>
/// <param name="Subtitle">The line under the title naming what was paid for.</param>
/// <param name="NumberLabel">What the document number is called on this paper, such as «شماره رسید».</param>
/// <param name="Highlight">One fact printed on a shaded row under the title, such as the operation on a surgery receipt.</param>
/// <param name="Signer">The doctor who issued a prescription, printed at its foot. Never a typed or drawn signature.</param>
public sealed record PrintDocument(
    string Title, string Number, DateTime Date, string Format, SettingsDto Hospital,
    IReadOnlyList<PrintField> Fields, IReadOnlyList<string>? Headers, IReadOnlyList<IReadOnlyList<string?>>? Rows,
    IReadOnlyList<PrintField>? Totals, string? Note,
    string? Subtitle = null, string? NumberLabel = null, PrintField? Highlight = null, string? Signer = null, string? SignerTitle = null);

/// <summary>
/// One document shape for every printout, so each document type only states its own content.
/// Each endpoint returns only what that paper needs: a receipt carries no diagnosis. The content follows the approved
/// print reference (alshefa_print_bundle): number, date and patient name, one amount band, and a further row only
/// when the money is not simply settled, so a partial, refunded or void paper never reads as fully paid.
/// </summary>
public static class PrintEndpoints
{
    private const string A4 = "a4";
    private const string Receipt = "receipt";

    private sealed record ReceiptKind(string Title, string? Subtitle, PrintField? Highlight);

    public static void MapPrint(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/print").WithTags("Print");

        group.MapGet("/visit/{id:long}", async (long id, AppDbContext db, CancellationToken ct) =>
        {
            var v = await db.Visits.AsNoTracking()
                .Where(x => x.Id == id)
                .Select(x => new { x.VisitNumber, x.QueueNumber, x.CreatedAt, x.Patient!.FileNumber, x.Patient.FirstName, x.Patient.LastName, Department = x.Department!.Name, Doctor = x.Doctor != null ? x.Doctor.FullName : null })
                .SingleOrDefaultAsync(ct) ?? throw AppException.NotFound();

            if (v.QueueNumber is null)
            {
                throw new AppException(StatusCodes.Status409Conflict, "no_queue_number", "پیش از پرداخت فیس، رسید نوبت چاپ نمی‌شود.");
            }

            return Results.Ok(new PrintDocument("رسید پذیرش", v.VisitNumber, v.CreatedAt, Receipt, await SettingsEndpoints.LoadAsync(db, ct),
            [
                new("نام مریض", Name(v.FirstName, v.LastName)),
                new("شماره پرونده", v.FileNumber),
                new("بخش", v.Department),
                new("داکتر", v.Doctor),
            ], null, null,
            [new("شماره نوبت", v.QueueNumber.Value.ToString(CultureInfo.InvariantCulture))], null,
            NumberLabel: "شماره مراجعه"));
        }).RequirePermission("visits.print");

        group.MapGet("/patient/{id:long}", async (long id, AppDbContext db, CancellationToken ct) =>
        {
            var p = await db.Patients.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw AppException.NotFound();
            return Results.Ok(new PrintDocument("کارت مریض", p.FileNumber, p.CreatedAt, Receipt, await SettingsEndpoints.LoadAsync(db, ct),
            [
                new("نام مریض", Name(p.FirstName, p.LastName)),
                new("نام پدر", p.FatherName),
                new("جنسیت", p.Gender switch { Gender.Male => "مرد", Gender.Female => "زن", _ => "دیگر" }),
                new("تماس", p.Phone),
            ], null, null, null, null,
            NumberLabel: "شماره پرونده"));
        }).RequirePermission("patients.print");

        group.MapGet("/rx/{id:long}", async (long id, AppDbContext db, CancellationToken ct) =>
        {
            var rx = await db.MedicalPrescriptions.AsNoTracking().Include(x => x.Items).Include(x => x.Patient)
                .SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw AppException.NotFound();
            var doctor = await db.Users.AsNoTracking().Where(u => u.Id == rx.DoctorUserId).Select(u => u.FullName).SingleOrDefaultAsync(ct);
            var specialty = await db.Staff.AsNoTracking().Where(s => s.UserId == rx.DoctorUserId).Select(s => s.Specialty).FirstOrDefaultAsync(ct);

            return Results.Ok(new PrintDocument("نسخه داکتر", rx.RxNumber, rx.CreatedAt, A4, await SettingsEndpoints.LoadAsync(db, ct),
            [
                new("نام مریض", Name(rx.Patient!.FirstName, rx.Patient.LastName)),
            ],
            ["دوا", "مقدار", "روش مصرف", "مدت", "توضیح", "تعداد"],
            rx.Items.OrderBy(i => i.Id).Select(i => (IReadOnlyList<string?>)[i.DrugName, i.Dose, i.Route, i.Duration, i.Instructions, Number(i.Quantity)]).ToList(),
            null, rx.Notes,
            NumberLabel: "شماره نسخه", Signer: doctor, SignerTitle: specialty));
        }).RequirePermission("prescriptions.print");

        // Read by the optical slip (OpticalSlipPrint.vue), which has its own layout; its labels are its contract.
        group.MapGet("/optical/{id:long}", async (long id, AppDbContext db, CancellationToken ct) =>
        {
            var rx = await db.OpticalPrescriptions.AsNoTracking().Include(x => x.Patient).SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw AppException.NotFound();
            var doctor = await db.Users.AsNoTracking().Where(u => u.Id == rx.DoctorUserId).Select(u => u.FullName).SingleOrDefaultAsync(ct);

            return Results.Ok(new PrintDocument("نسخه عینک", rx.RxNumber, rx.CreatedAt, A4, await SettingsEndpoints.LoadAsync(db, ct),
            [
                new("مریض", Name(rx.Patient!.FirstName, rx.Patient.LastName)),
                new("شماره پرونده", rx.Patient.FileNumber),
                new("داکتر", doctor),
                new("PD", rx.Pd),
            ],
            ["چشم", "Sphere", "Cylinder", "Axis", "ADD"],
            [
                ["راست (OD)", rx.SphereOd, rx.CylinderOd, rx.AxisOd, rx.AddOd],
                ["چپ (OS)", rx.SphereOs, rx.CylinderOs, rx.AxisOs, rx.AddOs],
            ],
            null, rx.Notes));
        }).RequirePermission("opticalrx.print");

        group.MapGet("/lab/{id:long}", async (long id, AppDbContext db, CancellationToken ct) =>
        {
            var o = await db.DiagnosticOrders.AsNoTracking()
                .Where(x => x.Id == id)
                .Select(x => new { x.OrderNumber, x.CreatedAt, x.Patient!.FileNumber, x.Patient.FirstName, x.Patient.LastName, Test = x.Test!.Name, Result = x.Result != null ? x.Result.ResultText : null, PerformedAt = x.Result != null ? (DateTime?)x.Result.PerformedAt : null })
                .SingleOrDefaultAsync(ct) ?? throw AppException.NotFound();
            if (o.Result is null)
            {
                throw new AppException(StatusCodes.Status409Conflict, "no_result", "برای این درخواست هنوز نتیجه ثبت نشده است.");
            }

            return Results.Ok(new PrintDocument("جواب آزمایش", o.OrderNumber, o.PerformedAt ?? o.CreatedAt, A4, await SettingsEndpoints.LoadAsync(db, ct),
            [
                new("نام مریض", Name(o.FirstName, o.LastName)),
                new("شماره پرونده", o.FileNumber),
            ], null, null, null, o.Result,
            NumberLabel: "شماره درخواست", Highlight: new("آزمایش", o.Test)));
        }).RequirePermission("diagnostics.print");

        group.MapGet("/invoice/{id:long}", async (long id, AppDbContext db, CancellationToken ct) =>
        {
            var i = await db.Invoices.AsNoTracking().Include(x => x.Patient).SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw AppException.NotFound();
            var items = await db.InvoiceItems.AsNoTracking().Where(x => x.InvoiceId == id).OrderBy(x => x.Id).ToListAsync(ct);
            var activeItems = items.Where(x => !x.IsVoid).ToList();
            var kind = await ReceiptKindAsync(db, id, ct);
            var invoiceTitle = kind.Title switch
            {
                "رسید عملیات" => "فاکتور خدمات عملیات",
                "رسید لابراتوار" => "بیل لابراتوار",
                _ => "فاکتور خدمات",
            };
            var withQuantity = activeItems.Any(x => x.Quantity != 1);

            return Results.Ok(new PrintDocument(invoiceTitle, i.InvoiceNumber, i.CreatedAt, A4, await SettingsEndpoints.LoadAsync(db, ct),
            [
                new("نام مریض", i.Patient is null ? null : Name(i.Patient.FirstName, i.Patient.LastName)),
                ..InvoiceStanding(i),
                new("آیتم‌های باطل‌شده", items.Any(x => x.IsVoid) ? items.Count(x => x.IsVoid).ToString(CultureInfo.InvariantCulture) : null),
            ],
            withQuantity ? ["#", "شرح خدمت", "مقدار", "مبلغ"] : ["#", "شرح خدمت", "مبلغ"],
            activeItems.Select((x, n) => (IReadOnlyList<string?>)(withQuantity
                ? [Index(n), x.Description, Number(x.Quantity), Money(x.LineTotal)]
                : [Index(n), x.Description, Money(x.LineTotal)])).ToList(),
            [new("مجموع", Money(i.Total - i.Discount))], null,
            NumberLabel: "شماره فاکتور", Highlight: kind.Highlight));
        }).RequirePermission("invoices.print");

        group.MapGet("/receipt/{id:long}", async (long id, AppDbContext db, CancellationToken ct) =>
        {
            var p = await db.Payments.AsNoTracking()
                .Where(x => x.Id == id)
                .Select(x => new { x.ReceiptNumber, x.Amount, x.Refunded, x.CreatedAt, x.InvoiceId, x.Invoice!.Status, x.Invoice.Total, x.Invoice.Discount, x.Invoice.Paid, InvoiceRefunded = x.Invoice.Refunded, Patient = x.Invoice.Patient })
                .SingleOrDefaultAsync(ct) ?? throw AppException.NotFound();
            var queueNumber = await db.VisitTokens.AsNoTracking().Where(t => t.InvoiceId == p.InvoiceId).Select(t => t.QueueNumber).FirstOrDefaultAsync(ct);
            var kind = await ReceiptKindAsync(db, p.InvoiceId, ct);
            var balance = Balance(p.Total, p.Discount, p.Paid, p.InvoiceRefunded);
            var receiptStatus = p.Status == InvoiceStatus.Void ? "باطل"
                : p.Refunded >= p.Amount ? "برگشت کامل"
                : p.Refunded > 0 ? "برگشت جزئی"
                : balance > 0 ? "پرداخت جزئی"
                : null;

            return Results.Ok(new PrintDocument(kind.Title, p.ReceiptNumber, p.CreatedAt, Receipt, await SettingsEndpoints.LoadAsync(db, ct),
            [
                new("نام مریض", p.Patient is null ? null : Name(p.Patient.FirstName, p.Patient.LastName)),
                new("شماره نوبت", queueNumber?.ToString(CultureInfo.InvariantCulture)),
                new("وضعیت", receiptStatus),
                new("برگشت وجه", p.Refunded > 0 ? Money(p.Refunded) : null),
                new("دریافت خالص", p.Refunded > 0 ? Money(p.Amount - p.Refunded) : null),
                new("مانده فاکتور", balance > 0 ? Money(balance) : null),
            ], null, null,
            [new("مبلغ", Money(p.Amount))], null,
            kind.Subtitle, "شماره رسید", kind.Highlight));
        }).RequirePermission("payments.print");

        group.MapGet("/refund/{id:long}", async (long id, AppDbContext db, HttpContext http, CancellationToken ct) =>
        {
            if (!http.User.HasClaim(RolePermissions.ClaimType, "payments.print") &&
                !http.User.HasClaim(RolePermissions.ClaimType, "payments.refund"))
            {
                throw new AppException(StatusCodes.Status403Forbidden, "forbidden", "شما اجازه این عملیات را ندارید.");
            }

            var refund = await db.Refunds.AsNoTracking()
                .Where(r => r.Id == id)
                .Select(r => new
                {
                    r.RefundNumber, r.Amount, r.CreatedAt, r.Payment!.ReceiptNumber,
                    Patient = r.Payment.Invoice!.Patient,
                })
                .SingleOrDefaultAsync(ct) ?? throw AppException.NotFound();

            return Results.Ok(new PrintDocument("رسید برگشت وجه", refund.RefundNumber, refund.CreatedAt, Receipt, await SettingsEndpoints.LoadAsync(db, ct),
            [
                new("نام مریض", refund.Patient is null ? null : Name(refund.Patient.FirstName, refund.Patient.LastName)),
                new("رسید پرداخت", refund.ReceiptNumber),
            ], null, null,
            [new("مبلغ برگشت", Money(refund.Amount))], null,
            NumberLabel: "شماره رسید"));
        }).RequireAuthorization();

        group.MapGet("/sale/{id:long}", async (long id, AppDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var s = await db.Sales.AsNoTracking().Include(x => x.Patient).Include(x => x.Invoice).SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw AppException.NotFound();
            var needed = s.Kind == SaleKind.Pharmacy ? "pharmacy.print" : "optical.print";
            if (!http.User.HasClaim(RolePermissions.ClaimType, needed))
            {
                throw new AppException(StatusCodes.Status403Forbidden, "forbidden", "شما اجازه این عملیات را ندارید.");
            }

            var items = await db.InvoiceItems.AsNoTracking().Where(x => x.InvoiceId == s.InvoiceId && !x.IsVoid).OrderBy(x => x.Id).ToListAsync(ct);
            var pharmacy = s.Kind == SaleKind.Pharmacy;
            var invoice = s.Invoice!;
            return Results.Ok(new PrintDocument(pharmacy ? "فاکتور دواخانه" : "رسید فروش عینک", pharmacy ? invoice.InvoiceNumber : s.SaleNumber, s.CreatedAt, pharmacy ? A4 : Receipt, await SettingsEndpoints.LoadAsync(db, ct),
            [
                new("نام مریض", s.Patient is null ? null : Name(s.Patient.FirstName, s.Patient.LastName)),
                ..InvoiceStanding(invoice),
            ],
            ["#", pharmacy ? "نام دوا" : "جنس", "مقدار", "مبلغ"],
            items.Select((x, n) => (IReadOnlyList<string?>)[Index(n), x.Description, Number(x.Quantity), Money(x.LineTotal)]).ToList(),
            [new("مجموع", Money(invoice.Total - invoice.Discount))], null,
            NumberLabel: pharmacy ? "شماره فاکتور" : "شماره فروش"));
        }).RequireAuthorization();
    }

    private static string Name(string first, string? last) => $"{first} {last}".Trim();

    private static string Index(int n) => (n + 1).ToString(CultureInfo.InvariantCulture);

    private static string Number(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Money(decimal value) => value.ToString("#,0.##", CultureInfo.InvariantCulture) + " افغانی";

    private static decimal Balance(Invoice invoice) => Balance(invoice.Total, invoice.Discount, invoice.Paid, invoice.Refunded);

    private static decimal Balance(decimal total, decimal discount, decimal paid, decimal refunded) =>
        Math.Max(0, total - discount - (paid - refunded));

    private static string InvoiceStatusLabel(Invoice invoice)
    {
        if (invoice.Status == InvoiceStatus.Void)
        {
            return "باطل";
        }

        var netPaid = invoice.Paid - invoice.Refunded;
        if (invoice.Refunded > 0 && netPaid <= 0)
        {
            return "برگشت کامل";
        }

        if (netPaid <= 0)
        {
            return "پرداخت‌نشده";
        }

        return Balance(invoice) > 0 ? (invoice.Refunded > 0 ? "پرداخت جزئی پس از برگشت" : "پرداخت جزئی") : "پرداخت‌شده";
    }

    /// <summary>
    /// The band shows the total after discount. A settled invoice adds nothing under it; any other state names itself,
    /// with the discount, payments, refunds and balance that explain it.
    /// </summary>
    private static IEnumerable<PrintField> InvoiceStanding(Invoice invoice)
    {
        var status = InvoiceStatusLabel(invoice);
        var settled = status == "پرداخت‌شده";
        yield return new("وضعیت", settled ? null : status);
        yield return new("جمع پیش از تخفیف", invoice.Discount > 0 ? Money(invoice.Total) : null);
        yield return new("تخفیف", invoice.Discount > 0 ? Money(invoice.Discount) : null);
        yield return new("پرداخت‌شده", settled || invoice.Paid <= 0 ? null : Money(invoice.Paid));
        yield return new("برگشت وجه", invoice.Refunded > 0 ? Money(invoice.Refunded) : null);
        yield return new("مانده", Balance(invoice) > 0 ? Money(Balance(invoice)) : null);
    }

    /// <summary>What the paid invoice was for, which names the receipt as in the print reference.</summary>
    private static async Task<ReceiptKind> ReceiptKindAsync(AppDbContext db, long invoiceId, CancellationToken ct)
    {
        var sources = await db.InvoiceItems.AsNoTracking()
            .Where(i => i.InvoiceId == invoiceId && !i.IsVoid)
            .Select(i => new { i.SourceType, i.SourceId, i.Description })
            .ToListAsync(ct);

        if (sources.Any(s => s.SourceType is nameof(VisitToken) or nameof(Visit)))
        {
            return new("رسید پرداخت", "فیس معاینه داکتر", null);
        }

        var surgery = sources.Where(s => s.SourceType == nameof(SurgeryCase)).Select(s => s.Description).ToList();
        if (surgery.Count > 0)
        {
            return new("رسید عملیات", null, new("نوع عملیات", string.Join("، ", surgery.Distinct())));
        }

        var diagnostics = sources.Where(s => s.SourceType == nameof(DiagnosticOrder) && s.SourceId.HasValue).ToList();
        if (diagnostics.Count > 0)
        {
            var ids = diagnostics.Select(s => s.SourceId!.Value).ToList();
            var kinds = await db.DiagnosticOrders.AsNoTracking()
                .Where(o => ids.Contains(o.Id))
                .Select(o => o.Test!.Kind)
                .Distinct()
                .ToListAsync(ct);
            if (kinds.Count == 1)
            {
                var names = string.Join("، ", diagnostics.Select(s => s.Description).Distinct());
                return kinds[0] switch
                {
                    DiagnosticKind.Refraction => new("رسید رفراکشن", "خدمات بینایی‌سنجی", null),
                    DiagnosticKind.Imaging => new("رسید تصویربرداری", $"تصویربرداری چشم / {names}", null),
                    DiagnosticKind.Lab => new("رسید لابراتوار", "خدمات آزمایشگاهی", null),
                    _ => new("رسید پرداخت", null, null),
                };
            }
        }

        return new("رسید پرداخت", null, null);
    }
}
