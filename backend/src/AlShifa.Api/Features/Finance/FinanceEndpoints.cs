using System.ComponentModel.DataAnnotations;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Visits;
using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AlShifa.Api.Features.Finance;

public sealed record SaveServiceRequest([Required, MaxLength(30)] string Code, [Required, MaxLength(150)] string Name, int? DepartmentId, bool IsActive, [Range(0, 100000000)] decimal? Price);

public sealed record EditServiceRequest([Required, MaxLength(30)] string Code, [Required, MaxLength(150)] string Name, bool IsActive);

public sealed record ServiceRow(int Id, string Code, string Name, string? DepartmentName, bool IsActive, decimal? CurrentPrice);

public sealed record CreateInvoiceRequest(long? PatientId, long? VisitId, [Required, MinLength(1)] InvoiceLineInput[] Items);

public sealed record InvoiceLineInput(int ServiceId, [Range(0.001, 100000)] decimal Quantity);

public sealed record DiscountRequest([Range(0.01, 100000000)] decimal Amount, [Required, MinLength(3), MaxLength(300)] string Reason, int Version);

public sealed record VoidRequest([Required, MaxLength(300)] string Reason, int Version);

public sealed record CorrectInvoiceRequest(long? PatientId, [Required, MinLength(1)] InvoiceLineInput[] Items, [Required, MinLength(3), MaxLength(300)] string Reason, int Version);

public sealed record CancelDiscountRequest([Required, MinLength(3), MaxLength(300)] string Reason, int Version);

public sealed record CorrectExpenseRequest([Required, MaxLength(100)] string Category, [MaxLength(300)] string? Description, [Range(0.01, 100000000)] decimal Amount, DateOnly ExpenseDate, [Required, MinLength(3), MaxLength(300)] string Reason, int Version);

/// <param name="InvoiceVersion">The version on the cashier's screen; a change since then means the amount was decided on old figures.</param>
public sealed record PaymentRequest(long InvoiceId, [Range(0.01, 100000000)] decimal Amount, PaymentMethod Method, [MaxLength(100)] string? Reference, int? InvoiceVersion = null);

public sealed record RefundRequest(long PaymentId, [Range(0.01, 100000000)] decimal Amount, [Required, MinLength(3), MaxLength(300)] string Reason);

public sealed record OpenSessionRequest(int RegisterId, [Range(0, 100000000)] decimal OpeningAmount);

public sealed record CloseSessionRequest([Range(0, 100000000)] decimal CountedAmount, int Version);

public sealed record CreateExpenseRequest([Required, MaxLength(100)] string Category, [MaxLength(300)] string? Description, [Range(0.01, 100000000)] decimal Amount, DateOnly ExpenseDate);

/// <param name="Description">What the first line charges for, so the cashier sees what the patient is paying for.</param>
/// <param name="ItemCount">The lines on the invoice, counting that first one.</param>
public sealed record InvoiceRow(long Id, string InvoiceNumber, long? PatientId, string? PatientFileNumber, string? PatientName, long? VisitId, string Status, decimal Total, decimal Discount, decimal Paid, decimal Refunded, decimal Balance, DateTime CreatedAt, int Version, string? Description, int ItemCount);

public sealed record InvoiceItemRow(long Id, string Description, decimal Quantity, decimal UnitPrice, decimal LineTotal, bool IsVoid, int? ServiceId = null);

/// <param name="QueueNumber">The number a visit fee earned, for the cashier to give the patient. Null until the fee is settled, and on any other invoice.</param>
public sealed record PaymentRow(long Id, string ReceiptNumber, long InvoiceId, string InvoiceNumber, decimal Amount, string Method, string? Reference, decimal Refunded, DateTime CreatedAt, string? ReceivedBy, int? QueueNumber);

public sealed record DiscountHistoryRow(string? Details, string? ByUser, DateTime CreatedAt);

/// <param name="Correctable">True for an invoice issued on its own. One raised by a sale, a request or a visit is corrected at its source.</param>
public sealed record InvoiceDetail(InvoiceRow Invoice, IReadOnlyList<InvoiceItemRow> Items, IReadOnlyList<PaymentRow> Payments, IReadOnlyList<DiscountHistoryRow> Discounts, bool Correctable = false);

public sealed record SessionRow(long Id, int RegisterId, string RegisterName, string? OpenedBy, decimal OpeningAmount, DateTime OpenedAt, DateTime? ClosedAt, decimal? ExpectedAmount, decimal? CountedAmount, decimal CashCollected, int Version);

public sealed record LedgerBalance(string Code, string Name, decimal Debit, decimal Credit);

public sealed record ReconciliationResult(bool Balanced, decimal TotalDebit, decimal TotalCredit, decimal LedgerReceivable, decimal InvoiceReceivable, decimal LedgerCash, decimal PaymentsLessRefundsAndExpenses);

public static class FinanceEndpoints
{
    public static void MapFinance(this IEndpointRouteBuilder app)
    {
        MapServices(app);
        MapInvoices(app);
        MapPayments(app);
        MapCash(app);
        MapLedger(app);
        MapExpenses(app);
    }

    private static void MapServices(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/services").WithTags("Finance");

        group.MapGet("/", async (AppDbContext db, CancellationToken ct) =>
        {
            var now = DateTime.UtcNow;
            var rows = await db.Services.AsNoTracking()
                .OrderBy(s => s.Name)
                .Select(s => new ServiceRow(
                    s.Id, s.Code, s.Name, s.Department != null ? s.Department.Name : null, s.IsActive,
                    db.ServicePrices.Where(p => p.ServiceId == s.Id && p.EffectiveFrom <= now).OrderByDescending(p => p.EffectiveFrom).ThenByDescending(p => p.Id).Select(p => (decimal?)p.Price).FirstOrDefault()))
                .ToListAsync(ct);
            return Results.Ok(rows);
        }).RequireAuthorization();

        group.MapPost("/", async (SaveServiceRequest request, AppDbContext db, CurrentUser user, AuditService audit, CancellationToken ct) =>
        {
            if (request.Price is { } requested)
            {
                Money.EnsureCents(requested);
            }

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var service = new Service { Code = request.Code.Trim(), Name = request.Name.Trim(), DepartmentId = request.DepartmentId, IsActive = request.IsActive };
            db.Services.Add(service);
            await db.SaveChangesAsync(ct);
            if (request.Price is { } price)
            {
                db.ServicePrices.Add(new ServicePrice { ServiceId = service.Id, Price = price, EffectiveFrom = DateTime.UtcNow, CreatedByUserId = user.Id });
            }

            audit.Add("ServiceCreated", "Service", service.Id.ToString());
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Created($"/api/services/{service.Id}", new { service.Id });
        }).RequirePermission("settings.edit");

        group.MapPost("/{id:int}/price", async (int id, SetPriceRequest request, AppDbContext db, CurrentUser user, AuditService audit, CancellationToken ct) =>
        {
            var service = await db.Services.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, ct) ?? throw AppException.NotFound();
            Money.EnsureCents(request.Price);

            // These fees open the visit and the paid departments, where zero means "not set" and refuses to charge.
            if (request.Price <= 0 && ServiceCodes.PaidFlow.Contains(service.Code))
            {
                throw new AppException(StatusCodes.Status400BadRequest, "price_required", "قیمت این خدمت باید بیشتر از صفر باشد.");
            }

            var previous = await db.ServicePrices.Where(p => p.ServiceId == id).OrderByDescending(p => p.EffectiveFrom).ThenByDescending(p => p.Id).Select(p => (decimal?)p.Price).FirstOrDefaultAsync(ct);
            db.ServicePrices.Add(new ServicePrice { ServiceId = id, Price = request.Price, EffectiveFrom = DateTime.UtcNow, CreatedByUserId = user.Id });
            audit.Add("ServicePriceChanged", "Service", id.ToString(), $"{previous?.ToString("F2") ?? "-"} => {request.Price:F2}");
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequirePermission("services.edit");

        group.MapPut("/{id:int}", async (int id, EditServiceRequest request, AppDbContext db, AuditService audit, CancellationToken ct) =>
        {
            var service = await db.Services.SingleOrDefaultAsync(s => s.Id == id, ct) ?? throw AppException.NotFound();
            var code = request.Code.Trim();
            var system = ServiceCodes.PaidFlow.Contains(service.Code);

            // Reception and the paid departments find these by code, and need them active to charge at all.
            if ((system && (code != service.Code || !request.IsActive)) || (!system && ServiceCodes.PaidFlow.Contains(code)))
            {
                throw new AppException(StatusCodes.Status409Conflict, "system_service", "کد این خدمت سیستمی است و خدمت باید فعال بماند. فقط نام آن قابل تغییر است.");
            }

            service.Code = code;
            service.Name = request.Name.Trim();
            service.IsActive = request.IsActive;
            audit.Add("ServiceEdited", "Service", id.ToString());
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequirePermission("settings.edit");

        group.MapDelete("/{id:int}", async (int id, AppDbContext db, AuditService audit, CancellationToken ct) =>
        {
            var service = await db.Services.SingleOrDefaultAsync(s => s.Id == id, ct) ?? throw AppException.NotFound();
            if (ServiceCodes.PaidFlow.Contains(service.Code))
            {
                throw new AppException(StatusCodes.Status409Conflict, "system_service", "این خدمت سیستمی است و حذف نمی‌شود.");
            }

            // Invoice lines copy the name and price and hold this id without a foreign key, so the database would not refuse.
            if (await db.InvoiceItems.AnyAsync(i => i.ServiceId == id, ct))
            {
                service.IsActive = false;
                audit.Add("ServiceDeactivated", "Service", id.ToString(), service.Name);
                await db.SaveChangesAsync(ct);
                return Results.Ok(new DeleteOutcome(false));
            }

            var prices = await db.ServicePrices.Where(p => p.ServiceId == id).ToListAsync(ct);
            return await SafeDelete.RemoveOrDeactivateAsync(db, audit, service, "Service", id.ToString(), service.Name, () => service.IsActive = false, ct, prices);
        }).RequirePermission("settings.edit");
    }

    public static async Task<decimal> CurrentPriceAsync(AppDbContext db, int serviceId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var price = await db.ServicePrices.Where(p => p.ServiceId == serviceId && p.EffectiveFrom <= now)
            .OrderByDescending(p => p.EffectiveFrom).ThenByDescending(p => p.Id)
            .Select(p => (decimal?)p.Price).FirstOrDefaultAsync(ct);
        return price ?? throw new AppException(StatusCodes.Status409Conflict, "no_price", "برای این خدمت قیمت تعیین نشده است.");
    }

    private static void MapInvoices(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/invoices").WithTags("Finance");

        group.MapGet("/", async (AppDbContext db, string? q, bool? unpaid, long? patientId, int page = 1, int pageSize = 50, CancellationToken ct = default) =>
        {
            pageSize = Math.Clamp(pageSize, 1, 100);
            page = Math.Max(page, 1);

            var query = db.Invoices.AsNoTracking();
            if (patientId is { } pid)
            {
                // The patient file asks by id: a file-number prefix would also match longer file numbers of other patients.
                query = query.Where(i => i.PatientId == pid);
            }

            if (unpaid == true)
            {
                // A discount can cover a whole invoice with nothing paid, and nothing is left to collect on it.
                query = query.Where(i => (i.Status == InvoiceStatus.Issued || i.Status == InvoiceStatus.PartiallyPaid) && i.Total - i.Discount - i.Paid > 0);
            }

            if (!string.IsNullOrWhiteSpace(q))
            {
                query = query.Where(i => i.InvoiceNumber.StartsWith(q) || (i.Patient != null && (i.Patient.FileNumber.StartsWith(q) || i.Patient.FirstName.StartsWith(q))));
            }

            var total = await query.CountAsync(ct);
            var rows = await Project(query.OrderByDescending(i => i.Id).Skip((page - 1) * pageSize).Take(pageSize)).ToListAsync(ct);
            return Results.Ok(new PagedResult<InvoiceRow>(rows, total, page, pageSize));
        }).RequirePermission("invoices.view");

        group.MapGet("/{id:long}", async (long id, AppDbContext db, CancellationToken ct) =>
        {
            var invoice = await Project(db.Invoices.AsNoTracking().Where(i => i.Id == id)).SingleOrDefaultAsync(ct) ?? throw AppException.NotFound();
            var items = await db.InvoiceItems.AsNoTracking().Where(i => i.InvoiceId == id).OrderBy(i => i.Id)
                .Select(i => new InvoiceItemRow(i.Id, i.Description, i.Quantity, i.UnitPrice, i.LineTotal, i.IsVoid, i.ServiceId)).ToListAsync(ct);
            var payments = await QueryPayments(db, db.Payments.Where(p => p.InvoiceId == id)).ToListAsync(ct);
            var key = id.ToString();
            var discounts = await (
                from a in db.AuditLogs.AsNoTracking()
                join u in db.Users on a.UserId equals u.Id
                where a.Action == "InvoiceDiscount" && a.EntityId == key
                orderby a.Id descending
                select new DiscountHistoryRow(a.Details, u.FullName, a.CreatedAt)).ToListAsync(ct);
            return Results.Ok(new InvoiceDetail(invoice, items, payments, discounts, await IsStandaloneAsync(db, id, ct)));
        }).RequirePermission("invoices.view");

        group.MapPost("/", async (CreateInvoiceRequest request, AppDbContext db, Idempotency idempotency, InvoiceService invoices, AuditService audit, CancellationToken ct) =>
            await idempotency.RunAsync(request, async () =>
            {
                var invoice = await IssueAsync(request.PatientId, request.VisitId, request.Items, db, invoices, audit, ct);
                return (StatusCodes.Status201Created, await Project(db.Invoices.AsNoTracking().Where(i => i.Id == invoice.Id)).SingleAsync(ct));
            }, ct)).RequirePermission("invoices.create");

        // Voids the mistaken invoice and issues the right one together, so neither can happen without the other.
        group.MapPost("/{id:long}/correct", async (long id, CorrectInvoiceRequest request, AppDbContext db, InvoiceService invoices, LedgerService ledger, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            Permission.Require(http, "invoices.create");
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var invoice = await invoices.LockAsync(id, ct);
            if (invoice.Version != request.Version)
            {
                throw new DbUpdateConcurrencyException();
            }

            if (!await IsStandaloneAsync(db, id, ct))
            {
                throw new AppException(StatusCodes.Status409Conflict, "invoice_not_standalone", "این فاکتور از بخش دیگری صادر شده است. آن را از همان بخش اصلاح یا لغو کنید.");
            }

            var visitId = request.PatientId == invoice.PatientId ? invoice.VisitId : null;
            await VoidAsync(invoice, request.Reason.Trim(), db, ledger, audit, ct);
            await db.SaveChangesAsync(ct);

            var replacement = await IssueAsync(request.PatientId, visitId, request.Items, db, invoices, audit, ct);
            audit.Add("InvoiceCorrected", "Invoice", id.ToString(), $"{replacement.InvoiceNumber}: {request.Reason.Trim()}");
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Ok(await Project(db.Invoices.AsNoTracking().Where(i => i.Id == replacement.Id)).SingleAsync(ct));
        }).RequirePermission("invoices.cancel");

        // Only before any payment: a discount already relied on at the cashier cannot be taken back from here.
        group.MapPost("/{id:long}/discount/cancel", async (long id, CancelDiscountRequest request, AppDbContext db, InvoiceService invoices, LedgerService ledger, AuditService audit, Outbox outbox, CancellationToken ct) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var invoice = await invoices.LockAsync(id, ct);
            if (invoice.Version != request.Version)
            {
                throw new DbUpdateConcurrencyException();
            }

            if (invoice.Status == InvoiceStatus.Void || invoice.Discount <= 0)
            {
                throw new AppException(StatusCodes.Status409Conflict, "no_discount", "این فاکتور تخفیف فعالی ندارد.");
            }

            if (invoice.Paid > 0)
            {
                throw new AppException(StatusCodes.Status409Conflict, "invoice_has_payments", "فاکتور دارای پرداخت است و تخفیف آن برگشت نمی‌شود.");
            }

            // A wholly discounted request counts as paid and may already have started.
            if (!await IsStandaloneAsync(db, id, ct) && !await db.Sales.AnyAsync(s => s.InvoiceId == id, ct))
            {
                throw new AppException(StatusCodes.Status409Conflict, "invoice_not_standalone", "تخفیف این فاکتور از اینجا برگشت نمی‌شود، چون خدمت مربوط ممکن است آغاز شده باشد.");
            }

            var amount = invoice.Discount;
            invoice.Discount = 0;
            InvoiceService.UpdateStatus(invoice);
            var earlier = await db.AuditLogs.CountAsync(a => a.Action == "InvoiceDiscountCancelled" && a.EntityId == id.ToString(), ct);
            audit.Add("InvoiceDiscountCancelled", "Invoice", id.ToString(), $"{amount:F2}: {request.Reason.Trim()}");
            ledger.Post($"DiscountUndo{earlier + 1}", "Invoice", id, request.Reason.Trim(),
                (Accounts.Receivable, amount, 0),
                (Accounts.Discounts, 0, amount));
            outbox.Add("InvoiceIssued", new { invoiceId = id }, Groups.Cashier, Groups.Finance, Groups.Reception);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        }).RequirePermission("invoices.approve");

        group.MapPost("/{id:long}/discount", async (long id, DiscountRequest request, AppDbContext db, InvoiceService invoices, VisitFeeService visitFees, LedgerService ledger, AuditService audit, Outbox outbox, CancellationToken ct) =>
        {
            Money.EnsureCents(request.Amount);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var fee = await visitFees.LockFeeOfInvoiceAsync(id, ct);
            var invoice = await invoices.LockAsync(id, ct);
            if (invoice.Version != request.Version)
            {
                throw new DbUpdateConcurrencyException();
            }

            if (invoice.Status == InvoiceStatus.Void)
            {
                throw new AppException(StatusCodes.Status409Conflict, "invoice_void", "این فاکتور باطل شده است.");
            }

            if (request.Amount > InvoiceService.Balance(invoice))
            {
                throw new AppException(StatusCodes.Status409Conflict, "discount_too_large", "تخفیف از مانده فاکتور بیشتر است.");
            }

            // A discount may lower the visit fee but never waive it.
            if (invoice.Total - invoice.Discount - request.Amount <= 0 && fee is not null)
            {
                throw new AppException(StatusCodes.Status409Conflict, "visit_fee_no_exemption", "فیس ویزیت معافیت ندارد. تخفیف نمی‌تواند تمام مبلغ را حذف کند.");
            }

            invoice.Discount += request.Amount;
            InvoiceService.UpdateStatus(invoice);
            var audited = await db.AuditLogs.CountAsync(a => a.Action == "InvoiceDiscount" && a.EntityId == id.ToString(), ct);
            audit.Add("InvoiceDiscount", "Invoice", id.ToString(), $"{request.Amount:F2}: {request.Reason}");
            ledger.Post($"Discount{audited + 1}", "Invoice", id, request.Reason,
                (Accounts.Discounts, request.Amount, 0),
                (Accounts.Receivable, 0, request.Amount));
            outbox.Add("InvoiceIssued", new { invoiceId = id }, Groups.Cashier, Groups.Finance, Groups.Reception);
            await db.SaveChangesAsync(ct);

            // A discount can finish a fee that was part paid at the cashier, and a settled fee earns its number.
            await visitFees.IssueNumberIfSettledAsync(fee, invoice, ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        }).RequirePermission("invoices.approve");

        group.MapPost("/{id:long}/void", async (long id, VoidRequest request, AppDbContext db, InvoiceService invoices, LedgerService ledger, AuditService audit, CancellationToken ct) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var invoice = await invoices.LockAsync(id, ct);
            if (invoice.Version != request.Version)
            {
                throw new DbUpdateConcurrencyException();
            }

            await VoidAsync(invoice, request.Reason, db, ledger, audit, ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        }).RequirePermission("invoices.cancel");
    }

    private static async Task<Invoice> IssueAsync(long? patientId, long? visitId, InvoiceLineInput[] items, AppDbContext db, InvoiceService invoices, AuditService audit, CancellationToken ct)
    {
        if (patientId is { } pid && !await db.Patients.AnyAsync(p => p.Id == pid, ct))
        {
            throw new AppException(StatusCodes.Status400BadRequest, "unknown_patient", "مریض پیدا نشد.");
        }

        if (visitId is { } vid && !await db.Visits.AnyAsync(v => v.Id == vid && v.PatientId == patientId, ct))
        {
            throw new AppException(StatusCodes.Status400BadRequest, "visit_mismatch", "این مراجعه به این مریض تعلق ندارد.");
        }

        var invoice = await invoices.CreateAsync(patientId, visitId, ct);
        foreach (var line in items)
        {
            var service = await db.Services.AsNoTracking().SingleOrDefaultAsync(s => s.Id == line.ServiceId && s.IsActive, ct)
                ?? throw new AppException(StatusCodes.Status400BadRequest, "unknown_service", "خدمت انتخاب‌شده معتبر نیست.");
            var price = await CurrentPriceAsync(db, service.Id, ct);
            await invoices.AddLineAsync(invoice, new ChargeLine(service.Name, line.Quantity, price, Accounts.ServiceRevenue, service.Id, null, null), ct);
        }

        audit.Add("InvoiceCreated", "Invoice", invoice.Id.ToString());
        await db.SaveChangesAsync(ct);
        return invoice;
    }

    /// <summary>Call with the invoice locked, inside a transaction.</summary>
    private static async Task VoidAsync(Invoice invoice, string reason, AppDbContext db, LedgerService ledger, AuditService audit, CancellationToken ct)
    {
        if (invoice.Paid > 0)
        {
            throw new AppException(StatusCodes.Status409Conflict, "invoice_has_payments", "فاکتور دارای پرداخت است و باطل نمی‌شود. برگشت وجه ثبت کنید.");
        }

        if (invoice.Status == InvoiceStatus.Void)
        {
            throw new AppException(StatusCodes.Status409Conflict, "invoice_void", "این فاکتور قبلاً باطل شده است.");
        }

        if (invoice.Discount > 0)
        {
            throw new AppException(StatusCodes.Status409Conflict, "invoice_has_discount", "فاکتور دارای تخفیف است و باطل نمی‌شود.");
        }

        var items = await db.InvoiceItems.Where(i => i.InvoiceId == invoice.Id && !i.IsVoid).ToListAsync(ct);
        foreach (var item in items)
        {
            item.IsVoid = true;
            await ledger.ReverseAsync("Charge", "InvoiceItem", item.Id, ct);
        }

        invoice.Status = InvoiceStatus.Void;
        invoice.Total = 0;
        audit.Add("InvoiceVoided", "Invoice", invoice.Id.ToString(), reason);
    }

    /// <summary>Lines from a sale, a request or a visit fee belong to that record, which still needs them.</summary>
    private static async Task<bool> IsStandaloneAsync(AppDbContext db, long id, CancellationToken ct) =>
        !await db.InvoiceItems.AnyAsync(i => i.InvoiceId == id && !i.IsVoid && (i.SourceType != null || i.ServiceId == null), ct)
        && !await db.VisitTokens.AnyAsync(t => t.InvoiceId == id, ct)
        && !await db.DiagnosticOrders.AnyAsync(o => o.InvoiceId == id, ct)
        && !await db.Sales.AnyAsync(s => s.InvoiceId == id, ct);

    private static void MapPayments(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/payments").WithTags("Finance");

        group.MapGet("/", async (AppDbContext db, long? sessionId, int page = 1, int pageSize = 50, CancellationToken ct = default) =>
        {
            pageSize = Math.Clamp(pageSize, 1, 100);
            page = Math.Max(page, 1);

            var query = db.Payments.AsNoTracking();
            if (sessionId is { } s)
            {
                query = query.Where(p => p.CashSessionId == s);
            }

            var total = await query.CountAsync(ct);
            var rows = await QueryPayments(db, query.OrderByDescending(p => p.Id).Skip((page - 1) * pageSize).Take(pageSize)).ToListAsync(ct);
            return Results.Ok(new PagedResult<PaymentRow>(rows, total, page, pageSize));
        }).RequirePermission("payments.view");

        group.MapPost("/", async (PaymentRequest request, AppDbContext db, Idempotency idempotency, InvoiceService invoices, VisitFeeService visitFees, CancellationToken ct) =>
            await idempotency.RunAsync(request, async () =>
            {
                var fee = await visitFees.LockFeeOfInvoiceAsync(request.InvoiceId, ct, forPayment: true);
                var payment = await invoices.ReceiveAsync(request, fullOnly: false, ct);
                await visitFees.IssueNumberIfSettledAsync(fee, await invoices.LockAsync(request.InvoiceId, ct), ct);
                var row = await QueryPayments(db, db.Payments.Where(p => p.Id == payment.Id)).SingleAsync(ct);
                return (StatusCodes.Status201Created, row);
            }, ct)).RequirePermission("payments.create");

        app.MapPost("/api/refunds", async (RefundRequest request, AppDbContext db, Idempotency idempotency, InvoiceService invoices, VisitFeeService visitFees, NumberService numbers, LedgerService ledger, CurrentUser user, AuditService audit, CancellationToken ct) =>
            await idempotency.RunAsync(request, async () =>
            {
                Money.EnsureCents(request.Amount);
                var invoiceId = await db.Payments.Where(p => p.Id == request.PaymentId).Select(p => (long?)p.InvoiceId).SingleOrDefaultAsync(ct) ?? throw AppException.NotFound();
                var fee = await visitFees.LockFeeOfInvoiceAsync(invoiceId, ct);
                var invoice = await invoices.LockAsync(invoiceId, ct);

                // Must be a locking read: a plain read here sees the data from before the wait for the invoice lock,
                // so simultaneous refunds would each see the full amount as still available.
                var payment = await db.Payments.FromSqlInterpolated($"SELECT * FROM Payments WHERE Id = {request.PaymentId} FOR UPDATE").SingleAsync(ct);

                if (request.Amount > payment.Amount - payment.Refunded)
                {
                    throw new AppException(StatusCodes.Status409Conflict, "refund_too_large", "مبلغ برگشت از پرداخت باقی‌مانده بیشتر است.");
                }

                var refund = new Refund
                {
                    RefundNumber = $"RF-{await numbers.NextAsync("refund", ct: ct):D7}",
                    PaymentId = payment.Id,
                    Amount = request.Amount,
                    Reason = request.Reason.Trim(),
                    ApprovedByUserId = user.Id,
                };
                db.Refunds.Add(refund);
                payment.Refunded += request.Amount;
                invoice.Refunded += request.Amount;
                await db.SaveChangesAsync(ct);

                ledger.Post("Refund", "Refund", refund.Id, refund.Reason,
                    (Accounts.Refunds, refund.Amount, 0),
                    (Accounts.Cash, 0, refund.Amount));
                audit.Add("RefundApproved", "Refund", refund.Id.ToString(), refund.Reason);
                await db.SaveChangesAsync(ct);
                await visitFees.WithdrawIfFullyRefundedAsync(fee, invoice, ct);
                return (StatusCodes.Status201Created, new { refund.Id, refund.RefundNumber });
            }, ct)).RequirePermission("payments.refund").WithTags("Finance");
    }

    private static void MapCash(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/cash").WithTags("Finance");

        group.MapGet("/registers", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok(await db.CashRegisters.AsNoTracking().Where(r => r.IsActive).OrderBy(r => r.Id).Select(r => new { r.Id, r.Name }).ToListAsync(ct)))
            .RequirePermission("cash.view");

        group.MapGet("/sessions", async (AppDbContext db, CurrentUser user, HttpContext http, bool? mine, CancellationToken ct) =>
        {
            var query = db.CashSessions.AsNoTracking();

            // The reception desk sees its own session only, never the other registers' figures.
            if (mine == true || !http.User.HasClaim(RolePermissions.ClaimType, "payments.view"))
            {
                var id = user.Id;
                query = query.Where(s => s.OpenedByUserId == id);
            }

            return Results.Ok(await QuerySessions(db, query.OrderByDescending(s => s.Id).Take(50)).ToListAsync(ct));
        }).RequirePermission("cash.view");

        group.MapPost("/sessions", async (OpenSessionRequest request, AppDbContext db, CurrentUser user, AuditService audit, CancellationToken ct) =>
        {
            Money.EnsureCents(request.OpeningAmount);
            if (!await db.CashRegisters.AnyAsync(r => r.Id == request.RegisterId && r.IsActive, ct))
            {
                throw new AppException(StatusCodes.Status400BadRequest, "unknown_register", "صندوق انتخاب‌شده معتبر نیست.");
            }

            var userId = user.Id;
            if (await db.CashSessions.AnyAsync(s => s.OpenedByUserId == userId && s.ClosedAt == null, ct))
            {
                throw new AppException(StatusCodes.Status409Conflict, "session_already_open", "شما یک صندوق باز دارید.");
            }

            var session = new CashSession { RegisterId = request.RegisterId, OpenedByUserId = userId, OpeningAmount = request.OpeningAmount };
            db.CashSessions.Add(session);
            audit.Add("CashSessionOpened", "CashSession", null, request.OpeningAmount.ToString("F2"));
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/cash/sessions/{session.Id}", new { session.Id });
        }).RequirePermission("cash.create");

        group.MapPost("/sessions/{id:long}/close", async (long id, CloseSessionRequest request, AppDbContext db, CurrentUser user, AuditService audit, CancellationToken ct) =>
        {
            Money.EnsureCents(request.CountedAmount);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var session = await db.CashSessions.FromSqlInterpolated($"SELECT * FROM CashSessions WHERE Id = {id} FOR UPDATE").SingleOrDefaultAsync(ct) ?? throw AppException.NotFound();
            db.Entry(session).Property(s => s.Version).OriginalValue = request.Version;
            if (session.ClosedAt is not null)
            {
                throw new AppException(StatusCodes.Status409Conflict, "session_closed", "این صندوق قبلاً بسته شده است.");
            }

            if (session.OpenedByUserId != user.Id)
            {
                throw new AppException(StatusCodes.Status403Forbidden, "not_session_owner", "فقط بازکننده صندوق می‌تواند آن را ببندد.");
            }

            var collected = await db.Payments.Where(p => p.CashSessionId == id && p.Method == PaymentMethod.Cash).SumAsync(p => (decimal?)p.Amount, ct) ?? 0;
            session.ExpectedAmount = session.OpeningAmount + collected;
            session.CountedAmount = request.CountedAmount;
            session.ClosedAt = DateTime.UtcNow;
            session.ClosedByUserId = user.Id;
            audit.Add("CashSessionClosed", "CashSession", id.ToString(), $"expected {session.ExpectedAmount:F2} counted {request.CountedAmount:F2}");
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Ok(await QuerySessions(db, db.CashSessions.AsNoTracking().Where(s => s.Id == id)).SingleAsync(ct));
        }).RequirePermission("cash.edit");
    }

    private static void MapLedger(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/ledger").WithTags("Finance");

        group.MapGet("/balances", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok(await db.LedgerAccounts.AsNoTracking()
                .OrderBy(a => a.Code)
                .Select(a => new LedgerBalance(
                    a.Code, a.Name,
                    db.LedgerEntries.Where(e => e.AccountCode == a.Code).Sum(e => (decimal?)e.Debit) ?? 0,
                    db.LedgerEntries.Where(e => e.AccountCode == a.Code).Sum(e => (decimal?)e.Credit) ?? 0))
                .ToListAsync(ct))).RequirePermission("ledger.view");

        group.MapGet("/reconciliation", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok(await ReconcileAsync(db, ct))).RequirePermission("ledger.view");
    }

    public static async Task<ReconciliationResult> ReconcileAsync(AppDbContext db, CancellationToken ct)
    {
        var debit = await db.LedgerEntries.SumAsync(e => (decimal?)e.Debit, ct) ?? 0;
        var credit = await db.LedgerEntries.SumAsync(e => (decimal?)e.Credit, ct) ?? 0;

        var receivableEntries = db.LedgerEntries.Where(e => e.AccountCode == Accounts.Receivable);
        var ledgerReceivable = (await receivableEntries.SumAsync(e => (decimal?)e.Debit, ct) ?? 0) - (await receivableEntries.SumAsync(e => (decimal?)e.Credit, ct) ?? 0);
        var invoiceReceivable = await db.Invoices.Where(i => i.Status != InvoiceStatus.Void).SumAsync(i => (decimal?)(i.Total - i.Discount - i.Paid), ct) ?? 0;

        var cashEntries = db.LedgerEntries.Where(e => e.AccountCode == Accounts.Cash);
        var ledgerCash = (await cashEntries.SumAsync(e => (decimal?)e.Debit, ct) ?? 0) - (await cashEntries.SumAsync(e => (decimal?)e.Credit, ct) ?? 0);
        var payments = await db.Payments.SumAsync(p => (decimal?)p.Amount, ct) ?? 0;
        var refunds = await db.Refunds.SumAsync(r => (decimal?)r.Amount, ct) ?? 0;
        var expenses = await db.Expenses.Where(e => e.Status == ExpenseStatus.Recorded).SumAsync(e => (decimal?)e.Amount, ct) ?? 0;
        var derivedCash = payments - refunds - expenses;

        return new ReconciliationResult(
            debit == credit && ledgerReceivable == invoiceReceivable && ledgerCash == derivedCash,
            debit, credit, ledgerReceivable, invoiceReceivable, ledgerCash, derivedCash);
    }

    private static void MapExpenses(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/expenses").WithTags("Finance");

        group.MapGet("/", async (AppDbContext db, int page = 1, int pageSize = 50, CancellationToken ct = default) =>
        {
            pageSize = Math.Clamp(pageSize, 1, 100);
            page = Math.Max(page, 1);
            var total = await db.Expenses.CountAsync(ct);
            var rows = await db.Expenses.AsNoTracking().OrderByDescending(e => e.Id).Skip((page - 1) * pageSize).Take(pageSize)
                .Select(e => new { e.Id, e.ExpenseNumber, e.Category, e.Description, e.Amount, e.ExpenseDate, Status = e.Status.ToString(), e.Version })
                .ToListAsync(ct);
            return Results.Ok(new { items = rows, total, page, pageSize });
        }).RequirePermission("expenses.view");

        group.MapPost("/", async (CreateExpenseRequest request, AppDbContext db, Idempotency idempotency, NumberService numbers, LedgerService ledger, CurrentUser user, AuditService audit, CancellationToken ct) =>
            await idempotency.RunAsync(request, async () =>
            {
                var expense = await RecordExpenseAsync(request.Category, request.Description, request.Amount, request.ExpenseDate, db, numbers, ledger, user, audit, ct);
                return (StatusCodes.Status201Created, new { expense.Id, expense.ExpenseNumber });
            }, ct)).RequirePermission("expenses.create");

        group.MapPost("/{id:long}/cancel", async (long id, VoidRequest request, AppDbContext db, LedgerService ledger, AuditService audit, CancellationToken ct) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await CancelExpenseAsync(id, request.Version, request.Reason, db, ledger, audit, ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        }).RequirePermission("expenses.cancel");

        // Cancels the mistaken expense and records the right one together, so the cash figures never show one without the other.
        group.MapPost("/{id:long}/correct", async (long id, CorrectExpenseRequest request, AppDbContext db, NumberService numbers, LedgerService ledger, CurrentUser user, AuditService audit, HttpContext http, CancellationToken ct) =>
        {
            Permission.Require(http, "expenses.create");
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await CancelExpenseAsync(id, request.Version, request.Reason.Trim(), db, ledger, audit, ct);
            var replacement = await RecordExpenseAsync(request.Category, request.Description, request.Amount, request.ExpenseDate, db, numbers, ledger, user, audit, ct);
            audit.Add("ExpenseCorrected", "Expense", id.ToString(), $"{replacement.ExpenseNumber}: {request.Reason.Trim()}");
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { replacement.Id, replacement.ExpenseNumber });
        }).RequirePermission("expenses.cancel");
    }

    private static async Task<Expense> RecordExpenseAsync(string category, string? description, decimal amount, DateOnly date, AppDbContext db, NumberService numbers, LedgerService ledger, CurrentUser user, AuditService audit, CancellationToken ct)
    {
        Money.EnsureCents(amount);
        var expense = new Expense
        {
            ExpenseNumber = $"EX-{await numbers.NextAsync("expense", ct: ct):D6}",
            Category = category.Trim(),
            Description = Text.Clean(description),
            Amount = amount,
            ExpenseDate = date,
            CreatedByUserId = user.Id,
        };
        db.Expenses.Add(expense);
        await db.SaveChangesAsync(ct);

        ledger.Post("Expense", "Expense", expense.Id, expense.Category,
            (Accounts.Expenses, expense.Amount, 0),
            (Accounts.Cash, 0, expense.Amount));
        audit.Add("ExpenseRecorded", "Expense", expense.Id.ToString());
        await db.SaveChangesAsync(ct);
        return expense;
    }

    private static async Task CancelExpenseAsync(long id, int version, string reason, AppDbContext db, LedgerService ledger, AuditService audit, CancellationToken ct)
    {
        var expense = await db.Expenses.SingleOrDefaultAsync(e => e.Id == id, ct) ?? throw AppException.NotFound();
        db.Entry(expense).Property(e => e.Version).OriginalValue = version;
        if (expense.Status == ExpenseStatus.Cancelled)
        {
            throw new AppException(StatusCodes.Status409Conflict, "already_cancelled", "این مصرف قبلاً لغو شده است.");
        }

        expense.Status = ExpenseStatus.Cancelled;
        await ledger.ReverseAsync("Expense", "Expense", id, ct);
        audit.Add("ExpenseCancelled", "Expense", id.ToString(), reason);
        await db.SaveChangesAsync(ct);
    }

    private static IQueryable<InvoiceRow> Project(IQueryable<Invoice> invoices) => invoices.Select(i => new InvoiceRow(
        i.Id, i.InvoiceNumber, i.PatientId, i.Patient != null ? i.Patient.FileNumber : null,
        i.Patient != null ? (i.Patient.FirstName + " " + (i.Patient.LastName ?? "")).Trim() : null,
        i.VisitId, i.Status.ToString(), i.Total, i.Discount, i.Paid, i.Refunded, i.Total - i.Discount - i.Paid, i.CreatedAt, i.Version,
        i.Items.Where(x => !x.IsVoid).OrderBy(x => x.Id).Select(x => x.Description).FirstOrDefault(),
        i.Items.Count(x => !x.IsVoid)));

    private static IQueryable<PaymentRow> QueryPayments(AppDbContext db, IQueryable<Payment> payments) =>
        from p in payments
        join u in db.Users on p.ReceivedByUserId equals u.Id
        select new PaymentRow(p.Id, p.ReceiptNumber, p.InvoiceId, p.Invoice!.InvoiceNumber, p.Amount, p.Method.ToString(), p.Reference, p.Refunded, p.CreatedAt, u.FullName,
            db.VisitTokens.Where(t => t.InvoiceId == p.InvoiceId).Select(t => t.QueueNumber).FirstOrDefault());

    internal static IQueryable<SessionRow> QuerySessions(AppDbContext db, IQueryable<CashSession> sessions) =>
        from s in sessions
        join u in db.Users on s.OpenedByUserId equals u.Id
        select new SessionRow(
            s.Id, s.RegisterId, s.Register!.Name, u.FullName, s.OpeningAmount, s.OpenedAt, s.ClosedAt, s.ExpectedAmount, s.CountedAmount,
            db.Payments.Where(p => p.CashSessionId == s.Id && p.Method == PaymentMethod.Cash).Sum(p => (decimal?)p.Amount) ?? 0,
            s.Version);
}

public sealed record SetPriceRequest([Range(0, 100000000)] decimal Price);
