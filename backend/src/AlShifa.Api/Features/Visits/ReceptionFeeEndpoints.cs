using System.ComponentModel.DataAnnotations;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Clinical;
using AlShifa.Api.Features.Finance;
using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AlShifa.Api.Features.Visits;

public sealed record CreateVisitTokenRequest(int DepartmentId, long? DoctorStaffId);

public sealed record CancelTokenRequest(int Version);

public sealed record VisitTokenRow(
    long Id, int? QueueNumber, DateOnly TokenDate, int DepartmentId, string DepartmentName, long? DoctorStaffId, string? DoctorName,
    long InvoiceId, string InvoiceNumber, decimal Total, decimal Discount, decimal Balance, int InvoiceVersion, bool Paid, int Version);

public sealed record CreateAdmissionRequest(long PatientId, int DepartmentId, long? DoctorStaffId, [MaxLength(300)] string? Reason, int? ServiceId = null);

/// <summary>One visit at the desk: who, where it stands, what is owed and the number it earned.</summary>
public sealed record AdmissionRow(
    long VisitId, string VisitNumber, int? QueueNumber, long PatientId, string PatientFileNumber, string PatientName,
    string DepartmentName, string? DoctorName, string Status, DateOnly VisitDate, long InvoiceId, string InvoiceNumber,
    decimal Total, decimal Discount, decimal Balance, int InvoiceVersion, bool Paid, string? ReceiptNumber);

/// <summary>A visit fee with its locks held, ready for a change on its invoice.</summary>
public sealed record LockedFee(VisitToken Token, Visit? Visit);

/// <summary>
/// The visit fee and the queue number it buys. The number is issued in the transaction that settles the fee, so a
/// rolled-back payment returns its number and a settled fee cannot end up with none. Lock order: visit, token, invoice.
/// </summary>
public sealed class VisitFeeService(AppDbContext db, DbContextOptions<AppDbContext> options, NumberService numbers, InvoiceService invoices, CurrentUser user, AuditService audit, Outbox outbox)
{
    public async Task<VisitToken> CreateTokenAsync(int departmentId, long? doctorStaffId, long? patientId, CancellationToken ct, long? visitId = null)
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
        var invoice = await invoices.CreateAsync(patientId, visitId, ct);
        var token = new VisitToken
        {
            TokenDate = KabulClock.Today,
            DepartmentId = departmentId,
            DoctorStaffId = doctorStaffId,
            InvoiceId = invoice.Id,
            VisitId = visitId,
            CreatedByUserId = user.Id,
        };
        db.VisitTokens.Add(token);
        await db.SaveChangesAsync(ct);

        await invoices.AddLineAsync(invoice, new ChargeLine(service.Name, 1, price, Accounts.ServiceRevenue, service.Id, nameof(VisitToken), token.Id), ct);
        audit.Add("VisitTokenCreated", "VisitToken", token.Id.ToString());
        await db.SaveChangesAsync(ct);
        return token;
    }

    /// <summary>
    /// Takes the locks a change to a fee needs, or returns null when the invoice is no visit fee. Call it before locking the
    /// invoice. With forPayment, also refuses a fee whose visit is from an earlier day: its number would be for a queue that is gone.
    /// </summary>
    public async Task<LockedFee?> LockFeeOfInvoiceAsync(long invoiceId, CancellationToken ct, bool forPayment = false)
    {
        var row = await db.VisitTokens.AsNoTracking().Where(t => t.InvoiceId == invoiceId).Select(t => new { t.Id, t.VisitId }).SingleOrDefaultAsync(ct);
        if (row is null)
        {
            return null;
        }

        var visit = row.VisitId is { } visitId ? await VisitService.LockAsync(db, visitId, ct) : null;
        var token = await LockTokenAsync(row.Id, ct);
        if (visit is null && token.VisitId is { } linkedMeanwhile)
        {
            visit = await VisitService.LockAsync(db, linkedMeanwhile, ct);
        }

        if (forPayment && visit is not null)
        {
            EnsureStillPayable(visit);
        }

        return new LockedFee(token, visit);
    }

    /// <summary>Recovery for a fee that was settled without its number. It takes no money and gives one number however often it is called.</summary>
    public async Task<AdmissionRow> RecoverNumberAsync(long visitId, CancellationToken ct)
    {
        var (fee, invoice) = await LockVisitFeeAsync(visitId, ct);
        if (!InvoiceService.IsSettled(invoice))
        {
            throw new AppException(StatusCodes.Status409Conflict, "payment_required", "فیس این مراجعه هنوز کامل پرداخت نشده است.");
        }

        await IssueNumberIfSettledAsync(fee, invoice, ct);
        return await LoadLockedAdmissionAsync(fee, invoice, ct);
    }

    public async Task IssueNumberIfSettledAsync(LockedFee? fee, Invoice invoice, CancellationToken ct)
    {
        if (fee is null || !InvoiceService.IsSettled(invoice))
        {
            return;
        }

        var (token, visit) = fee;
        if (token.QueueNumber is null)
        {
            var today = KabulClock.Today;
            token.QueueNumber = (int)await numbers.NextAsync(VisitService.QueueNumberScope, today.ToString("yyyyMMdd"), ct);
            token.TokenDate = today;
            audit.Add("QueueNumberIssued", "VisitToken", token.Id.ToString(), token.QueueNumber.ToString());
        }

        if (visit is { Status: VisitStatus.AwaitingPayment })
        {
            visit.QueueNumber = token.QueueNumber;
            db.VisitStatusHistory.Add(new VisitStatusHistory { VisitId = visit.Id, FromStatus = visit.Status, ToStatus = VisitStatus.Waiting, ChangedByUserId = user.Id });
            visit.Status = VisitStatus.Waiting;
            audit.Add("VisitStatusChanged", "Visit", visit.Id.ToString(), nameof(VisitStatus.Waiting));
            outbox.Add("VisitStatusChanged", new { visitId = visit.Id }, VisitService.EventGroups);
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Money fully back and no clinical work begun: the patient leaves the queue and may open a new visit. Later steps stay for a person to judge.</summary>
    public async Task WithdrawIfFullyRefundedAsync(LockedFee? fee, Invoice invoice, CancellationToken ct)
    {
        if (fee?.Visit is not { Status: VisitStatus.Waiting or VisitStatus.Registered } visit || invoice.Paid != invoice.Refunded)
        {
            return;
        }

        // Read on a separate connection: this transaction's snapshot predates the wait for the visit lock and would miss an exam just saved.
        await using var fresh = new AppDbContext(options);
        if (await fresh.PreExaminations.AnyAsync(p => p.VisitId == visit.Id, ct) || await fresh.DiagnosticOrders.AnyAsync(o => o.VisitId == visit.Id, ct))
        {
            return;
        }

        db.VisitStatusHistory.Add(new VisitStatusHistory { VisitId = visit.Id, FromStatus = visit.Status, ToStatus = VisitStatus.Cancelled, ChangedByUserId = user.Id });
        visit.Status = VisitStatus.Cancelled;
        audit.Add("VisitStatusChanged", "Visit", visit.Id.ToString(), $"{nameof(VisitStatus.Cancelled)}: refund");
        outbox.Add("VisitStatusChanged", new { visitId = visit.Id }, VisitService.EventGroups);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>An unpaid fee closes with its cancelled visit, so nothing stays owed for it. A paid one stays on record until refunded.</summary>
    public async Task CloseUnpaidFeeAsync(long visitId, CancellationToken ct)
    {
        var tokenId = await db.VisitTokens.AsNoTracking().Where(t => t.VisitId == visitId).Select(t => (long?)t.Id).SingleOrDefaultAsync(ct);
        if (tokenId is not { } id)
        {
            return;
        }

        var token = await LockTokenAsync(id, ct);
        var invoice = await invoices.LockAsync(token.InvoiceId, ct);
        if (invoice.Paid > 0 || invoice.Status == InvoiceStatus.Void)
        {
            return;
        }

        var charge = await db.InvoiceItems.SingleOrDefaultAsync(i => i.InvoiceId == invoice.Id && !i.IsVoid, ct);
        if (charge is not null)
        {
            await invoices.ClosePrepaidAsync(charge, ct);
        }
    }

    public async Task<AdmissionRow> LoadAdmissionAsync(long visitId, CancellationToken ct) =>
        await Admissions(db, db.Visits.AsNoTracking().Where(v => v.Id == visitId)).SingleOrDefaultAsync(ct) ?? throw AppException.NotFound();

    public static IQueryable<AdmissionRow> Admissions(AppDbContext db, IQueryable<Visit> visits) =>
        from v in visits
        join t in db.VisitTokens on v.Id equals t.VisitId
        select new AdmissionRow(
            v.Id, v.VisitNumber, v.QueueNumber, v.PatientId, v.Patient!.FileNumber, (v.Patient.FirstName + " " + (v.Patient.LastName ?? "")).Trim(),
            v.Department!.Name, v.Doctor != null ? v.Doctor.FullName : null, v.Status.ToString(), v.VisitDate, t.InvoiceId, t.Invoice!.InvoiceNumber,
            t.Invoice.Total, t.Invoice.Discount, t.Invoice.Total - t.Invoice.Discount - t.Invoice.Paid, t.Invoice.Version,
            t.Invoice.Status != InvoiceStatus.Void && t.Invoice.Total - t.Invoice.Discount <= t.Invoice.Paid - t.Invoice.Refunded,
            db.Payments.Where(p => p.InvoiceId == t.InvoiceId).OrderByDescending(p => p.Id).Select(p => p.ReceiptNumber).FirstOrDefault());

    /// <summary>
    /// The answer of a recovery. Whatever can change is taken from the locked rows: the transaction's snapshot may predate the
    /// wait for those locks, and a second click would otherwise be told there is no number while the first one's stands.
    /// </summary>
    private async Task<AdmissionRow> LoadLockedAdmissionAsync(LockedFee fee, Invoice invoice, CancellationToken ct)
    {
        var visit = fee.Visit!;
        var row = await LoadAdmissionAsync(visit.Id, ct);
        var receipts = await db.Payments.FromSqlInterpolated($"SELECT * FROM Payments WHERE InvoiceId = {invoice.Id} ORDER BY Id DESC LIMIT 1 FOR SHARE").AsNoTracking().ToListAsync(ct);
        return row with
        {
            QueueNumber = visit.QueueNumber,
            Status = visit.Status.ToString(),
            Total = invoice.Total,
            Discount = invoice.Discount,
            Balance = InvoiceService.Balance(invoice),
            InvoiceVersion = invoice.Version,
            Paid = InvoiceService.IsSettled(invoice),
            ReceiptNumber = receipts.FirstOrDefault()?.ReceiptNumber,
        };
    }

    private async Task<(LockedFee Fee, Invoice Invoice)> LockVisitFeeAsync(long visitId, CancellationToken ct)
    {
        var visit = await VisitService.LockAsync(db, visitId, ct);
        if (visit.Status == VisitStatus.Cancelled)
        {
            throw new AppException(StatusCodes.Status409Conflict, "visit_closed", "این مراجعه لغو شده است.");
        }

        var tokens = await db.VisitTokens.FromSqlInterpolated($"SELECT * FROM VisitTokens WHERE VisitId = {visitId} FOR UPDATE").ToListAsync(ct);
        var token = tokens.SingleOrDefault() ?? throw new AppException(StatusCodes.Status409Conflict, "no_visit_fee", "این مراجعه فیس ویزیت ندارد.");
        return (new LockedFee(token, visit), await invoices.LockAsync(token.InvoiceId, ct));
    }

    private Task<VisitToken> LockTokenAsync(long id, CancellationToken ct) =>
        db.VisitTokens.FromSqlInterpolated($"SELECT * FROM VisitTokens WHERE Id = {id} FOR UPDATE").SingleAsync(ct);

    private static void EnsureStillPayable(Visit visit)
    {
        if (visit.Status == VisitStatus.AwaitingPayment && visit.VisitDate != KabulClock.Today)
        {
            throw new AppException(StatusCodes.Status409Conflict, "visit_expired", "این مراجعه از روز گذشته مانده است. آن را لغو و مراجعه امروز را ثبت کنید.");
        }
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
/// The reception desk admits a patient, which sends the visit fee to the cashier. Reception takes no money: every fee in
/// the hospital is collected at the cashier, and the payment of the visit fee issues the queue number.
/// </summary>
public static class ReceptionFeeEndpoints
{
    public static void MapReceptionFees(this IEndpointRouteBuilder app)
    {
        var admissions = app.MapGroup("/api/admissions").WithTags("Reception");

        // Oldest first. Yesterday's unpaid visit stays listed, because only a person may cancel it.
        admissions.MapGet("/pending", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok(await VisitFeeService.Admissions(db, db.Visits.AsNoTracking().Where(v => v.Status == VisitStatus.AwaitingPayment).OrderBy(v => v.Id).Take(100)).ToListAsync(ct)))
            .RequirePermission("visitfees.view");

        admissions.MapGet("/{visitId:long}", async (long visitId, VisitFeeService visitFees, CancellationToken ct) =>
            Results.Ok(await visitFees.LoadAdmissionAsync(visitId, ct))).RequirePermission("visitfees.view");

        admissions.MapPost("/", async (CreateAdmissionRequest request, Idempotency idempotency, VisitService visits, VisitFeeService visitFees, CancellationToken ct) =>
            await idempotency.RunAsync(request, async () =>
            {
                var visit = await visits.AdmitAsync(request, ct);
                return (StatusCodes.Status201Created, await visitFees.LoadAdmissionAsync(visit.Id, ct));
            }, ct)).RequirePermission("visits.create").RequirePermission("visitfees.create");

        admissions.MapPost("/{visitId:long}/issue-number", async (long visitId, AppDbContext db, VisitFeeService visitFees, CancellationToken ct) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var row = await visitFees.RecoverNumberAsync(visitId, ct);
            await tx.CommitAsync(ct);
            return Results.Ok(row);
        }).RequirePermission("visitfees.create");

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
    }

    private static IQueryable<VisitTokenRow> Rows(IQueryable<VisitToken> tokens) => tokens.Select(t => new VisitTokenRow(
        t.Id, t.QueueNumber, t.TokenDate, t.DepartmentId, t.Department!.Name, t.DoctorStaffId, t.Doctor != null ? t.Doctor.FullName : null,
        t.InvoiceId, t.Invoice!.InvoiceNumber, t.Invoice.Total, t.Invoice.Discount, t.Invoice.Total - t.Invoice.Discount - t.Invoice.Paid, t.Invoice.Version,
        t.Invoice.Status != InvoiceStatus.Void && t.Invoice.Total - t.Invoice.Discount <= t.Invoice.Paid - t.Invoice.Refunded, t.Version));
}
