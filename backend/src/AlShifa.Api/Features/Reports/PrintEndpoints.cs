using System.Globalization;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Clinical;
using AlShifa.Api.Features.Finance;
using AlShifa.Api.Features.Inventory;
using AlShifa.Api.Features.Patients;
using AlShifa.Api.Features.Settings;
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

            return Results.Ok(new PrintDocument("رسید پذیرش", v.VisitNumber, v.CreatedAt, Receipt, await SettingsEndpoints.LoadAsync(db, ct),
            [
                new("مریض", Name(v.FirstName, v.LastName)),
                new("شماره پرونده", v.FileNumber),
                new("بخش", v.Department),
                new("داکتر", v.Doctor),
                new("شماره نوبت", v.QueueNumber.ToString(CultureInfo.InvariantCulture)),
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
            var items = await db.InvoiceItems.AsNoTracking().Where(x => x.InvoiceId == id && !x.IsVoid).OrderBy(x => x.Id).ToListAsync(ct);

            return Results.Ok(new PrintDocument("فاکتور خدمات", i.InvoiceNumber, i.CreatedAt, A4, await SettingsEndpoints.LoadAsync(db, ct),
            [
                new("مریض", i.Patient is null ? null : Name(i.Patient.FirstName, i.Patient.LastName)),
                new("شماره پرونده", i.Patient?.FileNumber),
            ],
            ["شرح", "تعداد", "قیمت واحد", "مبلغ"],
            items.Select(x => (IReadOnlyList<string?>)[x.Description, Number(x.Quantity), Money(x.UnitPrice), Money(x.LineTotal)]).ToList(),
            [
                new("مجموع", Money(i.Total)),
                new("تخفیف", Money(i.Discount)),
                new("پرداخت‌شده", Money(i.Paid)),
                new("مانده", Money(i.Total - i.Discount - i.Paid)),
            ], null));
        }).RequirePermission("invoices.print");

        group.MapGet("/receipt/{id:long}", async (long id, AppDbContext db, CancellationToken ct) =>
        {
            var p = await db.Payments.AsNoTracking()
                .Where(x => x.Id == id)
                .Select(x => new { x.ReceiptNumber, x.Amount, x.Method, x.CreatedAt, x.Invoice!.InvoiceNumber, x.Invoice.Total, x.Invoice.Discount, x.Invoice.Paid, Patient = x.Invoice.Patient, x.ReceivedByUserId })
                .SingleOrDefaultAsync(ct) ?? throw AppException.NotFound();
            var cashier = await db.Users.AsNoTracking().Where(u => u.Id == p.ReceivedByUserId).Select(u => u.FullName).SingleOrDefaultAsync(ct);

            return Results.Ok(new PrintDocument("رسید پرداخت", p.ReceiptNumber, p.CreatedAt, Receipt, await SettingsEndpoints.LoadAsync(db, ct),
            [
                new("مریض", p.Patient is null ? null : Name(p.Patient.FirstName, p.Patient.LastName)),
                new("فاکتور", p.InvoiceNumber),
                new("روش", p.Method == PaymentMethod.Cash ? "نقد" : "غیرنقدی"),
                new("صندوق‌دار", cashier),
            ], null, null,
            [
                new("مبلغ دریافت", Money(p.Amount)),
                new("مانده فاکتور", Money(p.Total - p.Discount - p.Paid)),
            ], null));
        }).RequirePermission("payments.print");

        group.MapGet("/sale/{id:long}", async (long id, AppDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var s = await db.Sales.AsNoTracking().Include(x => x.Patient).Include(x => x.Invoice).SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw AppException.NotFound();
            var needed = s.Kind == SaleKind.Pharmacy ? "pharmacy.print" : "optical.print";
            if (!http.User.HasClaim(RolePermissions.ClaimType, needed))
            {
                throw new AppException(StatusCodes.Status403Forbidden, "forbidden", "شما اجازه این عملیات را ندارید.");
            }

            var items = await db.InvoiceItems.AsNoTracking().Where(x => x.InvoiceId == s.InvoiceId && !x.IsVoid).OrderBy(x => x.Id).ToListAsync(ct);
            return Results.Ok(new PrintDocument(s.Kind == SaleKind.Pharmacy ? "رسید فروش دوا" : "رسید فروش عینک", s.SaleNumber, s.CreatedAt, Receipt, await SettingsEndpoints.LoadAsync(db, ct),
            [
                new("مریض", s.Patient is null ? null : Name(s.Patient.FirstName, s.Patient.LastName)),
                new("فاکتور", s.Invoice!.InvoiceNumber),
            ],
            ["جنس", "تعداد", "مبلغ"],
            items.Select(x => (IReadOnlyList<string?>)[x.Description, Number(x.Quantity), Money(x.LineTotal)]).ToList(),
            [
                new("مجموع", Money(s.Invoice.Total)),
                new("پرداخت‌شده", Money(s.Invoice.Paid)),
                new("مانده", Money(s.Invoice.Total - s.Invoice.Discount - s.Invoice.Paid)),
            ], null));
        }).RequireAuthorization();
    }

    private static string Name(string first, string? last) => $"{first} {last}".Trim();

    private static string Number(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Money(decimal value) => value.ToString("#,0.##", CultureInfo.InvariantCulture) + " افغانی";
}
