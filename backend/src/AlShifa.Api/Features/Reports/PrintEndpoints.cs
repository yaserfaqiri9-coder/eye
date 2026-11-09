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

public sealed record PrintDocument(
    string Title, string Number, DateTime Date, string Format, SettingsDto Hospital,
    IReadOnlyList<PrintField> Fields, IReadOnlyList<string>? Headers, IReadOnlyList<IReadOnlyList<string?>>? Rows,
    IReadOnlyList<PrintField>? Totals, string? Note);

/// <summary>
/// One document shape for every printout, so each document type only states its own content.
/// Each endpoint returns only what that paper needs: a receipt carries no diagnosis.
/// </summary>
public static class PrintEndpoints
{
    private const string A4 = "a4";
    private const string Receipt = "receipt";

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
                new("مریض", Name(v.FirstName, v.LastName)),
                new("شماره پرونده", v.FileNumber),
                new("بخش", v.Department),
                new("داکتر", v.Doctor),
                new("شماره نوبت", v.QueueNumber.Value.ToString(CultureInfo.InvariantCulture)),
            ], null, null, null, null));
        }).RequirePermission("visits.print");

        group.MapGet("/patient/{id:long}", async (long id, AppDbContext db, CancellationToken ct) =>
        {
            var p = await db.Patients.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw AppException.NotFound();
            return Results.Ok(new PrintDocument("کارت مریض", p.FileNumber, p.CreatedAt, Receipt, await SettingsEndpoints.LoadAsync(db, ct),
            [
                new("نام", Name(p.FirstName, p.LastName)),
                new("نام پدر", p.FatherName),
                new("جنسیت", p.Gender switch { Gender.Male => "مرد", Gender.Female => "زن", _ => "دیگر" }),
                new("تماس", p.Phone),
            ], null, null, null, null));
        }).RequirePermission("patients.print");

        group.MapGet("/rx/{id:long}", async (long id, AppDbContext db, CancellationToken ct) =>
        {
            var rx = await db.MedicalPrescriptions.AsNoTracking().Include(x => x.Items).Include(x => x.Patient)
                .SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw AppException.NotFound();
            var doctor = await db.Users.AsNoTracking().Where(u => u.Id == rx.DoctorUserId).Select(u => u.FullName).SingleOrDefaultAsync(ct);

            return Results.Ok(new PrintDocument("نسخه دوا", rx.RxNumber, rx.CreatedAt, A4, await SettingsEndpoints.LoadAsync(db, ct),
            [
                new("مریض", Name(rx.Patient!.FirstName, rx.Patient.LastName)),
                new("شماره پرونده", rx.Patient.FileNumber),
                new("داکتر", doctor),
            ],
            ["دوا", "مقدار", "روش مصرف", "مدت", "توضیح", "تعداد"],
            rx.Items.OrderBy(i => i.Id).Select(i => (IReadOnlyList<string?>)[i.DrugName, i.Dose, i.Route, i.Duration, i.Instructions, Number(i.Quantity)]).ToList(),
            null, rx.Notes));
        }).RequirePermission("prescriptions.print");

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
                new("مریض", Name(o.FirstName, o.LastName)),
                new("شماره پرونده", o.FileNumber),
                new("آزمایش", o.Test),
            ], null, null, null, o.Result));
        }).RequirePermission("diagnostics.print");

        group.MapGet("/invoice/{id:long}", async (long id, AppDbContext db, CancellationToken ct) =>
        {
            var i = await db.Invoices.AsNoTracking().Include(x => x.Patient).SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw AppException.NotFound();
            var items = await db.InvoiceItems.AsNoTracking().Where(x => x.InvoiceId == id).OrderBy(x => x.Id).ToListAsync(ct);
            var activeItems = items.Where(x => !x.IsVoid).ToList();
            var receiptTitle = await ReceiptTitleAsync(db, id, ct);
            var invoiceTitle = receiptTitle switch
            {
                "رسید خدمات عملیات" => "فاکتور خدمات عملیات",
                "رسید لابراتوار" => "بیل لابراتوار",
                _ => "فاکتور خدمات",
            };

            return Results.Ok(new PrintDocument(invoiceTitle, i.InvoiceNumber, i.CreatedAt, A4, await SettingsEndpoints.LoadAsync(db, ct),
            [
                new("مریض", i.Patient is null ? null : Name(i.Patient.FirstName, i.Patient.LastName)),
                new("شماره پرونده", i.Patient?.FileNumber),
                new("وضعیت", InvoiceStatusLabel(i)),
                new("آیتم‌های باطل‌شده", items.Any(x => x.IsVoid) ? items.Count(x => x.IsVoid).ToString(CultureInfo.InvariantCulture) : null),
            ],
            ["شرح", "تعداد", "قیمت واحد", "مبلغ"],
            activeItems.Select(x => (IReadOnlyList<string?>)[x.Description, Number(x.Quantity), Money(x.UnitPrice), Money(x.LineTotal)]).ToList(),
            InvoiceTotals(i), null));
        }).RequirePermission("invoices.print");

        group.MapGet("/receipt/{id:long}", async (long id, AppDbContext db, CancellationToken ct) =>
        {
            var p = await db.Payments.AsNoTracking()
                .Where(x => x.Id == id)
                .Select(x => new { x.ReceiptNumber, x.Amount, x.Refunded, x.Method, x.CreatedAt, x.InvoiceId, x.Invoice!.InvoiceNumber, x.Invoice.Status, x.Invoice.Total, x.Invoice.Discount, x.Invoice.Paid, InvoiceRefunded = x.Invoice.Refunded, Patient = x.Invoice.Patient, x.ReceivedByUserId })
                .SingleOrDefaultAsync(ct) ?? throw AppException.NotFound();
            var cashier = await db.Users.AsNoTracking().Where(u => u.Id == p.ReceivedByUserId).Select(u => u.FullName).SingleOrDefaultAsync(ct);
            var queueNumber = await db.VisitTokens.AsNoTracking().Where(t => t.InvoiceId == p.InvoiceId).Select(t => t.QueueNumber).FirstOrDefaultAsync(ct);
            var title = await ReceiptTitleAsync(db, p.InvoiceId, ct);
            var balance = Balance(p.Total, p.Discount, p.Paid, p.InvoiceRefunded);
            var receiptStatus = p.Status == InvoiceStatus.Void ? "باطل"
                : p.Refunded >= p.Amount ? "برگشت کامل"
                : p.Refunded > 0 ? "برگشت جزئی"
                : balance > 0 ? "پرداخت جزئی"
                : "پرداخت‌شده";

            return Results.Ok(new PrintDocument(title, p.ReceiptNumber, p.CreatedAt, Receipt, await SettingsEndpoints.LoadAsync(db, ct),
            [
                new("مریض", p.Patient is null ? null : Name(p.Patient.FirstName, p.Patient.LastName)),
                new("فاکتور", p.InvoiceNumber),
                new("شماره نوبت", queueNumber?.ToString(CultureInfo.InvariantCulture)),
                new("روش", p.Method == PaymentMethod.Cash ? "نقد" : "غیرنقدی"),
                new("صندوق‌دار", cashier),
                new("وضعیت", receiptStatus),
            ], null, null,
            [
                new("مبلغ دریافت", Money(p.Amount)),
                new("برگشت وجه", p.Refunded > 0 ? Money(p.Refunded) : null),
                new("دریافت خالص", p.Refunded > 0 ? Money(p.Amount - p.Refunded) : null),
                new("مانده فاکتور", Money(balance)),
            ], null));
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
                    r.Payment.Invoice!.InvoiceNumber,
                    Patient = r.Payment.Invoice.Patient,
                })
                .SingleOrDefaultAsync(ct) ?? throw AppException.NotFound();

            return Results.Ok(new PrintDocument("رسید برگشت وجه", refund.RefundNumber, refund.CreatedAt, Receipt, await SettingsEndpoints.LoadAsync(db, ct),
            [
                new("مریض", refund.Patient is null ? null : Name(refund.Patient.FirstName, refund.Patient.LastName)),
                new("فاکتور", refund.InvoiceNumber),
                new("رسید پرداخت", refund.ReceiptNumber),
            ], null, null,
            [new("مبلغ برگشت", Money(refund.Amount))], null));
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
                new("مریض", s.Patient is null ? null : Name(s.Patient.FirstName, s.Patient.LastName)),
                new(pharmacy ? "شماره فروش" : "فاکتور", pharmacy ? s.SaleNumber : invoice.InvoiceNumber),
                new("وضعیت", InvoiceStatusLabel(invoice)),
            ],
            pharmacy ? ["دوا", "تعداد", "قیمت واحد", "مبلغ"] : ["جنس", "تعداد", "مبلغ"],
            items.Select(x => (IReadOnlyList<string?>)(pharmacy
                ? [x.Description, Number(x.Quantity), Money(x.UnitPrice), Money(x.LineTotal)]
                : [x.Description, Number(x.Quantity), Money(x.LineTotal)])).ToList(),
            InvoiceTotals(invoice), null));
        }).RequireAuthorization();
    }

    private static string Name(string first, string? last) => $"{first} {last}".Trim();

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

    private static IReadOnlyList<PrintField> InvoiceTotals(Invoice invoice) =>
    [
        new("مجموع", Money(invoice.Total)),
        new("تخفیف", invoice.Discount > 0 ? Money(invoice.Discount) : null),
        new("پرداخت‌شده", Money(invoice.Paid)),
        new("برگشت وجه", invoice.Refunded > 0 ? Money(invoice.Refunded) : null),
        new("مانده", Money(Balance(invoice))),
    ];

    private static async Task<string> ReceiptTitleAsync(AppDbContext db, long invoiceId, CancellationToken ct)
    {
        var sources = await db.InvoiceItems.AsNoTracking()
            .Where(i => i.InvoiceId == invoiceId && !i.IsVoid)
            .Select(i => new { i.SourceType, i.SourceId })
            .ToListAsync(ct);

        if (sources.Any(s => s.SourceType is nameof(VisitToken) or nameof(Visit)))
        {
            return "رسید فیس معاینه داکتر";
        }

        if (sources.Any(s => s.SourceType == nameof(SurgeryCase)))
        {
            return "رسید خدمات عملیات";
        }

        var diagnosticIds = sources
            .Where(s => s.SourceType == nameof(DiagnosticOrder) && s.SourceId.HasValue)
            .Select(s => s.SourceId!.Value)
            .ToList();
        if (diagnosticIds.Count > 0)
        {
            var kinds = await db.DiagnosticOrders.AsNoTracking()
                .Where(o => diagnosticIds.Contains(o.Id))
                .Select(o => o.Test!.Kind)
                .Distinct()
                .ToListAsync(ct);
            if (kinds.Count == 1)
            {
                return kinds[0] switch
                {
                    DiagnosticKind.Refraction => "رسید پرداخت رفرکشن",
                    DiagnosticKind.Imaging => "رسید پرداخت تصویربرداری",
                    DiagnosticKind.Lab => "رسید لابراتوار",
                    _ => "رسید پرداخت",
                };
            }
        }

        return "رسید پرداخت";
    }
}
