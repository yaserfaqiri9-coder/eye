using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Clinical;
using AlShifa.Api.Features.Finance;
using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AlShifa.Api.Features.Visits;

public sealed record CreateVisitTokenRequest(int DepartmentId, long? DoctorStaffId);

public sealed record CancelTokenRequest(int Version);

public sealed record VisitTokenRow(
    long Id, int QueueNumber, DateOnly TokenDate, int DepartmentId, string DepartmentName, long? DoctorStaffId, string? DoctorName,
    long InvoiceId, string InvoiceNumber, decimal Total, decimal Discount, decimal Balance, int InvoiceVersion, bool Paid, int Version);

public sealed record ReceptionFeeRow(long InvoiceId, string InvoiceNumber, string? PatientName, string? PatientFileNumber, string Description, decimal Total, decimal Discount, decimal Balance, int InvoiceVersion);

/// <summary>Shared visit-fee workflow used by both walk-in tokens and appointment arrivals.</summary>
public sealed class VisitFeeService(AppDbContext db, NumberService numbers, InvoiceService invoices, CurrentUser user, AuditService audit)
{
    public async Task<VisitToken> CreateTokenAsync(int departmentId, long? doctorStaffId, long? patientId, CancellationToken ct)
    {
        if (!await db.Departments.AnyAsync(d => d.Id == departmentId && d.IsActive, ct))
        {
            throw new AppException(StatusCodes.Status400BadRequest, "unknown_department", "بخش انتخاب‌شده معتبر نیست.");
        }

        if (doctorStaffId is { } doctorId && !await db.Staff.AnyAsync(s => s.Id == doctorId && s.IsDoctor && s.IsActive, ct))
        {
            throw new AppException(StatusCodes.Status400BadRequest, "unknown_doctor", "داکتر انتخاب‌شده معتبر نیست.");
        }

        var service = await VisitFeeServiceAsync(db, ct);
        var price = await VisitFeePriceAsync(db, service, ct);
        var today = KabulClock.Today;
        var invoice = await invoices.CreateAsync(patientId, null, ct);
        var token = new VisitToken
        {
            QueueNumber = (int)await numbers.NextAsync(VisitService.QueueNumberScope, today.ToString("yyyyMMdd"), ct),
            TokenDate = today,
            DepartmentId = departmentId,
            DoctorStaffId = doctorStaffId,
            InvoiceId = invoice.Id,
            CreatedByUserId = user.Id,
        };
        db.VisitTokens.Add(token);
        await db.SaveChangesAsync(ct);

        await invoices.AddLineAsync(invoice, new ChargeLine(service.Name, 1, price, Accounts.ServiceRevenue, service.Id, nameof(VisitToken), token.Id), ct);
        audit.Add("VisitTokenCreated", "VisitToken", token.Id.ToString());
        await db.SaveChangesAsync(ct);
        return token;
    }

    public async Task<Payment> CollectFullAsync(VisitToken token, CancellationToken ct)
    {
        var invoice = await invoices.LockAsync(token.InvoiceId, ct);
        var payment = await invoices.ReceiveAsync(new PaymentRequest(invoice.Id, InvoiceService.Balance(invoice), PaymentMethod.Cash, null, invoice.Version), fullOnly: true, ct);
        audit.Add("ReceptionFeeCollected", "Invoice", invoice.Id.ToString(), $"{payment.Amount:F2} {payment.ReceiptNumber}");
        await db.SaveChangesAsync(ct);
        return payment;
    }

    internal static async Task<Service> VisitFeeServiceAsync(AppDbContext db, CancellationToken ct) =>
        await db.Services.AsNoTracking().SingleOrDefaultAsync(s => s.Code == ServiceCodes.VisitFee && s.IsActive, ct)
        ?? throw new AppException(StatusCodes.Status409Conflict, "no_price", "برای فیس ویزیت قیمت تعیین نشده است.");

    internal static async Task<decimal> VisitFeePriceAsync(AppDbContext db, Service service, CancellationToken ct)
    {
        var price = await FinanceEndpoints.CurrentPriceAsync(db, service.Id, ct);
        return price > 0 ? price : throw new AppException(StatusCodes.Status409Conflict, "no_price", "برای فیس ویزیت قیمت تعیین نشده است.");
    }
}

/// <summary>
/// The reception desk's limited finance: a token with its visit fee, and collecting fees for visit tokens and prepaid
/// requests only. Every payment goes through the same ledger, audit and cash-session path as the cashier's.
/// </summary>
public static class ReceptionFeeEndpoints
{
    public static void MapReceptionFees(this IEndpointRouteBuilder app)
    {
        var tokens = app.MapGroup("/api/visit-tokens").WithTags("Reception");

        tokens.MapGet("/fee", async (AppDbContext db, CancellationToken ct) =>
        {
            var service = await VisitFeeService.VisitFeeServiceAsync(db, ct);
            return Results.Ok(new { service.Name, Price = await VisitFeeService.VisitFeePriceAsync(db, service, ct) });
        }).RequirePermission("visitfees.view");

        // The newest hundred, oldest first: abandoned tokens of past days must not push today's off the list.
        // A token whose money has gone back is finished and can no longer open a visit.
        tokens.MapGet("/", async (AppDbContext db, CancellationToken ct) =>
        {
            var rows = await Rows(db.VisitTokens.AsNoTracking()
                .Where(t => t.VisitId == null && t.Invoice!.Status != InvoiceStatus.Void && (t.Invoice.Paid == 0 || t.Invoice.Paid > t.Invoice.Refunded))
                .OrderByDescending(t => t.Id)
                .Take(100)).ToListAsync(ct);
            rows.Reverse();
            return Results.Ok(rows);
        }).RequirePermission("visitfees.view");

        tokens.MapGet("/{id:long}", async (long id, AppDbContext db, CancellationToken ct) =>
            Results.Ok(await Rows(db.VisitTokens.AsNoTracking().Where(t => t.Id == id)).SingleOrDefaultAsync(ct) ?? throw AppException.NotFound()))
            .RequirePermission("visitfees.view");

        tokens.MapPost("/", async (CreateVisitTokenRequest request, AppDbContext db, Idempotency idempotency, VisitFeeService visitFees, CancellationToken ct) =>
            await idempotency.RunAsync(request, async () =>
            {
                var token = await visitFees.CreateTokenAsync(request.DepartmentId, request.DoctorStaffId, null, ct);
                return (StatusCodes.Status201Created, await Rows(db.VisitTokens.AsNoTracking().Where(t => t.Id == token.Id)).SingleAsync(ct));
            }, ct)).RequirePermission("visitfees.create");

        tokens.MapPost("/{id:long}/cancel", async (long id, CancelTokenRequest request, AppDbContext db, InvoiceService invoices, AuditService audit, CancellationToken ct) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var token = await db.VisitTokens.SingleOrDefaultAsync(t => t.Id == id, ct) ?? throw AppException.NotFound();
            db.Entry(token).Property(t => t.Version).OriginalValue = request.Version;
            var invoice = await invoices.LockAsync(token.InvoiceId, ct);
            if (token.VisitId is not null || invoice.Paid > 0)
            {
                throw new AppException(StatusCodes.Status409Conflict, "token_in_use", "این نوبت پرداخت یا استفاده شده و قابل لغو نیست.");
            }

            var charge = await db.InvoiceItems.SingleOrDefaultAsync(i => i.InvoiceId == invoice.Id && !i.IsVoid, ct);
            if (charge is not null)
            {
                await invoices.ClosePrepaidAsync(charge, ct);
            }

            audit.Add("VisitTokenCancelled", "VisitToken", id.ToString());
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        }).RequirePermission("visitfees.create");

        var fees = app.MapGroup("/api/reception-fees").WithTags("Reception");

        fees.MapGet("/", async (AppDbContext db, CancellationToken ct) =>
        {
            var rows = await db.DiagnosticOrders.AsNoTracking()
                .Where(o => o.Test!.Kind != DiagnosticKind.Lab && o.Invoice != null && o.Invoice.Status != InvoiceStatus.Void && o.Invoice.Total - o.Invoice.Discount - o.Invoice.Paid > 0)
                .OrderBy(o => o.Id)
                .Take(100)
                .Select(o => new { o.Invoice!.Id, o.Invoice.InvoiceNumber, o.Patient!.FirstName, o.Patient.LastName, o.Patient.FileNumber, o.Test!.Name, o.Invoice.Total, o.Invoice.Discount, o.Invoice.Paid, o.Invoice.Version })
                .ToListAsync(ct);
            return Results.Ok(rows.Select(r => new ReceptionFeeRow(r.Id, r.InvoiceNumber, $"{r.FirstName} {r.LastName}".Trim(), r.FileNumber, r.Name, r.Total, r.Discount, r.Total - r.Discount - r.Paid, r.Version)));
        }).RequirePermission("visitfees.view");

        fees.MapPost("/pay", async (PaymentRequest request, AppDbContext db, Idempotency idempotency, InvoiceService invoices, AuditService audit, CancellationToken ct) =>
            await idempotency.RunAsync(request, async () =>
            {
                var ownFee = await db.VisitTokens.AnyAsync(t => t.InvoiceId == request.InvoiceId, ct) || await db.DiagnosticOrders.AnyAsync(o => o.InvoiceId == request.InvoiceId && o.Test!.Kind != DiagnosticKind.Lab, ct);
                if (!ownFee)
                {
                    throw new AppException(StatusCodes.Status403Forbidden, "not_a_reception_fee", "این فاکتور باید در صندوق دریافت شود.");
                }

                if (request.InvoiceVersion is null)
                {
                    throw new AppException(StatusCodes.Status400BadRequest, "version_required", "نسخه رکورد ارسال نشده است.");
                }

                var payment = await invoices.ReceiveAsync(request, fullOnly: true, ct);
                audit.Add("ReceptionFeeCollected", "Invoice", request.InvoiceId.ToString(), $"{payment.Amount:F2} {payment.ReceiptNumber}");
                await db.SaveChangesAsync(ct);
                return (StatusCodes.Status201Created, new { payment.Id, payment.ReceiptNumber, payment.InvoiceId, payment.Amount });
            }, ct)).RequirePermission("visitfees.create");
    }

    private static IQueryable<VisitTokenRow> Rows(IQueryable<VisitToken> tokens) => tokens.Select(t => new VisitTokenRow(
        t.Id, t.QueueNumber, t.TokenDate, t.DepartmentId, t.Department!.Name, t.DoctorStaffId, t.Doctor != null ? t.Doctor.FullName : null,
        t.InvoiceId, t.Invoice!.InvoiceNumber, t.Invoice.Total, t.Invoice.Discount, t.Invoice.Total - t.Invoice.Discount - t.Invoice.Paid, t.Invoice.Version,
        t.Invoice.Status != InvoiceStatus.Void && t.Invoice.Total - t.Invoice.Discount <= t.Invoice.Paid - t.Invoice.Refunded, t.Version));
}
