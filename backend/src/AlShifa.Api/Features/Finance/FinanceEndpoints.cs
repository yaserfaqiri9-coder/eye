using System.ComponentModel.DataAnnotations;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AlShifa.Api.Features.Finance;

public sealed record SaveServiceRequest([Required, MaxLength(30)] string Code, [Required, MaxLength(150)] string Name, int? DepartmentId, bool IsActive, [Range(0, 100000000)] decimal? Price);

public sealed record ServiceRow(int Id, string Code, string Name, string? DepartmentName, bool IsActive, decimal? CurrentPrice);

public sealed record CreateInvoiceRequest(long? PatientId, long? VisitId, [Required, MinLength(1)] InvoiceLineInput[] Items);

public sealed record InvoiceLineInput(int ServiceId, [Range(0.001, 100000)] decimal Quantity);

public sealed record DiscountRequest([Range(0, 100000000)] decimal Amount, [Required, MaxLength(300)] string Reason, int Version);

public sealed record VoidRequest([Required, MaxLength(300)] string Reason, int Version);

public sealed record PaymentRequest(long InvoiceId, [Range(0.01, 100000000)] decimal Amount, PaymentMethod Method, [MaxLength(100)] string? Reference);

public sealed record RefundRequest(long PaymentId, [Range(0.01, 100000000)] decimal Amount, [Required, MinLength(3), MaxLength(300)] string Reason);

public sealed record OpenSessionRequest(int RegisterId, [Range(0, 100000000)] decimal OpeningAmount);

public sealed record CloseSessionRequest([Range(0, 100000000)] decimal CountedAmount, int Version);

public sealed record CreateExpenseRequest([Required, MaxLength(100)] string Category, [MaxLength(300)] string? Description, [Range(0.01, 100000000)] decimal Amount, DateOnly ExpenseDate);

public sealed record InvoiceRow(long Id, string InvoiceNumber, long? PatientId, string? PatientFileNumber, string? PatientName, long? VisitId, string Status, decimal Total, decimal Discount, decimal Paid, decimal Refunded, decimal Balance, DateTime CreatedAt, int Version);

public sealed record InvoiceItemRow(long Id, string Description, decimal Quantity, decimal UnitPrice, decimal LineTotal, bool IsVoid);

public sealed record PaymentRow(long Id, string ReceiptNumber, long InvoiceId, string InvoiceNumber, decimal Amount, string Method, string? Reference, decimal Refunded, DateTime CreatedAt, string? ReceivedBy);

public sealed record InvoiceDetail(InvoiceRow Invoice, IReadOnlyList<InvoiceItemRow> Items, IReadOnlyList<PaymentRow> Payments);

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
            if (!await db.Services.AnyAsync(s => s.Id == id, ct))
            {
                throw AppException.NotFound();
            }

            db.ServicePrices.Add(new ServicePrice { ServiceId = id, Price = request.Price, EffectiveFrom = DateTime.UtcNow, CreatedByUserId = user.Id });
            audit.Add("ServicePriceChanged", "Service", id.ToString(), request.Price.ToString("F2"));
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
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

        group.MapGet("/", async (AppDbContext db, string? q, bool? unpaid, int page = 1, int pageSize = 50, CancellationToken ct = default) =>
        {
            pageSize = Math.Clamp(pageSize, 1, 100);
            page = Math.Max(page, 1);

            var query = db.Invoices.AsNoTracking();
            if (unpaid == true)
            {
                query = query.Where(i => i.Status == InvoiceStatus.Issued || i.Status == InvoiceStatus.PartiallyPaid);
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
                .Select(i => new InvoiceItemRow(i.Id, i.Description, i.Quantity, i.UnitPrice, i.LineTotal, i.IsVoid)).ToListAsync(ct);
            var payments = await QueryPayments(db, db.Payments.Where(p => p.InvoiceId == id)).ToListAsync(ct);
            return Results.Ok(new InvoiceDetail(invoice, items, payments));
        }).RequirePermission("invoices.view");

        group.MapPost("/", async (CreateInvoiceRequest request, AppDbContext db, Idempotency idempotency, InvoiceService invoices, AuditService audit, CancellationToken ct) =>
            await idempotency.RunAsync(request, async () =>
            {
                if (request.PatientId is { } pid && !await db.Patients.AnyAsync(p => p.Id == pid, ct))
                {
                    throw new AppException(StatusCodes.Status400BadRequest, "unknown_patient", "مریض پیدا نشد.");
                }

                if (request.VisitId is { } vid && !await db.Visits.AnyAsync(v => v.Id == vid && v.PatientId == request.PatientId, ct))
                {
                    throw new AppException(StatusCodes.Status400BadRequest, "visit_mismatch", "این مراجعه به این مریض تعلق ندارد.");
                }

                var invoice = await invoices.CreateAsync(request.PatientId, request.VisitId, ct);
                foreach (var line in request.Items)
                {
                    var service = await db.Services.AsNoTracking().SingleOrDefaultAsync(s => s.Id == line.ServiceId && s.IsActive, ct)
                        ?? throw new AppException(StatusCodes.Status400BadRequest, "unknown_service", "خدمت انتخاب‌شده معتبر نیست.");
                    var price = await CurrentPriceAsync(db, service.Id, ct);
                    await invoices.AddLineAsync(invoice, new ChargeLine(service.Name, line.Quantity, price, Accounts.ServiceRevenue, service.Id, null, null), ct);
                }

                audit.Add("InvoiceCreated", "Invoice", invoice.Id.ToString());
                await db.SaveChangesAsync(ct);
                return (StatusCodes.Status201Created, await Project(db.Invoices.AsNoTracking().Where(i => i.Id == invoice.Id)).SingleAsync(ct));
            }, ct)).RequirePermission("invoices.create");

        group.MapPost("/{id:long}/discount", async (long id, DiscountRequest request, AppDbContext db, InvoiceService invoices, LedgerService ledger, AuditService audit, CancellationToken ct) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var invoice = await invoices.LockAsync(id, ct);
            if (invoice.Version != request.Version)
            {
                throw new DbUpdateConcurrencyException();
            }

            if (request.Amount > InvoiceService.Balance(invoice))
            {
                throw new AppException(StatusCodes.Status409Conflict, "discount_too_large", "تخفیف از مانده فاکتور بیشتر است.");
            }

            invoice.Discount += request.Amount;
            InvoiceService.UpdateStatus(invoice);
            var audited = await db.AuditLogs.CountAsync(a => a.Action == "InvoiceDiscount" && a.EntityId == id.ToString(), ct);
            audit.Add("InvoiceDiscount", "Invoice", id.ToString(), $"{request.Amount:F2}: {request.Reason}");
            ledger.Post($"Discount{audited + 1}", "Invoice", id, request.Reason,
                (Accounts.Discounts, request.Amount, 0),
                (Accounts.Receivable, 0, request.Amount));
            await db.SaveChangesAsync(ct);
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

            var items = await db.InvoiceItems.Where(i => i.InvoiceId == id && !i.IsVoid).ToListAsync(ct);
            foreach (var item in items)
            {
                item.IsVoid = true;
                await ledger.ReverseAsync("Charge", "InvoiceItem", item.Id, ct);
            }

            invoice.Status = InvoiceStatus.Void;
            invoice.Total = 0;
            audit.Add("InvoiceVoided", "Invoice", id.ToString(), request.Reason);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        }).RequirePermission("invoices.cancel");
    }

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

        group.MapPost("/", async (PaymentRequest request, AppDbContext db, Idempotency idempotency, InvoiceService invoices, NumberService numbers, LedgerService ledger, CurrentUser user, AuditService audit, Outbox outbox, CancellationToken ct) =>
            await idempotency.RunAsync(request, async () =>
            {
                var userId = user.Id;
                var session = await db.CashSessions.AsNoTracking()
                    .Where(s => s.OpenedByUserId == userId && s.ClosedAt == null)
                    .Select(s => (long?)s.Id).FirstOrDefaultAsync(ct)
                    ?? throw new AppException(StatusCodes.Status409Conflict, "no_open_session", "ابتدا صندوق را باز کنید.");

                var invoice = await invoices.LockAsync(request.InvoiceId, ct);
                if (invoice.Status == InvoiceStatus.Void)
                {
                    throw new AppException(StatusCodes.Status409Conflict, "invoice_void", "این فاکتور باطل شده است.");
                }

                if (request.Amount > InvoiceService.Balance(invoice))
                {
                    throw new AppException(StatusCodes.Status409Conflict, "overpayment", "مبلغ پرداخت از مانده فاکتور بیشتر است.");
                }

                var payment = new Payment
                {
                    ReceiptNumber = $"RC-{await numbers.NextAsync("receipt", ct: ct):D7}",
                    InvoiceId = invoice.Id,
                    Amount = request.Amount,
                    Method = request.Method,
                    Reference = Text.Clean(request.Reference),
                    CashSessionId = session,
                    ReceivedByUserId = userId,
                };
                db.Payments.Add(payment);
                invoice.Paid += request.Amount;
                InvoiceService.UpdateStatus(invoice);
                await db.SaveChangesAsync(ct);

                ledger.Post("Payment", "Payment", payment.Id, payment.ReceiptNumber,
                    (Accounts.Cash, payment.Amount, 0),
                    (Accounts.Receivable, 0, payment.Amount));
                audit.Add("PaymentReceived", "Payment", payment.Id.ToString());
                outbox.Add("PaymentReceived", new { invoiceId = invoice.Id }, Groups.Cashier, Groups.Finance, Groups.Management);
                await db.SaveChangesAsync(ct);

                var row = await QueryPayments(db, db.Payments.Where(p => p.Id == payment.Id)).SingleAsync(ct);
                return (StatusCodes.Status201Created, row);
            }, ct)).RequirePermission("payments.create");

        app.MapPost("/api/refunds", async (RefundRequest request, AppDbContext db, Idempotency idempotency, InvoiceService invoices, NumberService numbers, LedgerService ledger, CurrentUser user, AuditService audit, CancellationToken ct) =>
            await idempotency.RunAsync(request, async () =>
            {
                var invoiceId = await db.Payments.Where(p => p.Id == request.PaymentId).Select(p => (long?)p.InvoiceId).SingleOrDefaultAsync(ct) ?? throw AppException.NotFound();
                var invoice = await invoices.LockAsync(invoiceId, ct);
                var payment = await db.Payments.SingleAsync(p => p.Id == request.PaymentId, ct);

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
                return (StatusCodes.Status201Created, new { refund.Id, refund.RefundNumber });
            }, ct)).RequirePermission("payments.refund").WithTags("Finance");
    }

    private static void MapCash(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/cash").WithTags("Finance");

        group.MapGet("/registers", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok(await db.CashRegisters.AsNoTracking().Where(r => r.IsActive).OrderBy(r => r.Id).Select(r => new { r.Id, r.Name }).ToListAsync(ct)))
            .RequirePermission("cash.view");

        group.MapGet("/sessions", async (AppDbContext db, CurrentUser user, bool? mine, CancellationToken ct) =>
        {
            var query = db.CashSessions.AsNoTracking();
            if (mine == true)
            {
                var id = user.Id;
                query = query.Where(s => s.OpenedByUserId == id);
            }

            return Results.Ok(await QuerySessions(db, query.OrderByDescending(s => s.Id).Take(50)).ToListAsync(ct));
        }).RequirePermission("cash.view");

        group.MapPost("/sessions", async (OpenSessionRequest request, AppDbContext db, CurrentUser user, AuditService audit, CancellationToken ct) =>
        {
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
            var session = await db.CashSessions.SingleOrDefaultAsync(s => s.Id == id, ct) ?? throw AppException.NotFound();
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
                var expense = new Expense
                {
                    ExpenseNumber = $"EX-{await numbers.NextAsync("expense", ct: ct):D6}",
                    Category = request.Category.Trim(),
                    Description = Text.Clean(request.Description),
                    Amount = request.Amount,
                    ExpenseDate = request.ExpenseDate,
                    CreatedByUserId = user.Id,
                };
                db.Expenses.Add(expense);
                await db.SaveChangesAsync(ct);

                ledger.Post("Expense", "Expense", expense.Id, expense.Category,
                    (Accounts.Expenses, expense.Amount, 0),
                    (Accounts.Cash, 0, expense.Amount));
                audit.Add("ExpenseRecorded", "Expense", expense.Id.ToString());
                await db.SaveChangesAsync(ct);
                return (StatusCodes.Status201Created, new { expense.Id, expense.ExpenseNumber });
            }, ct)).RequirePermission("expenses.create");

        group.MapPost("/{id:long}/cancel", async (long id, VoidRequest request, AppDbContext db, LedgerService ledger, AuditService audit, CancellationToken ct) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var expense = await db.Expenses.SingleOrDefaultAsync(e => e.Id == id, ct) ?? throw AppException.NotFound();
            db.Entry(expense).Property(e => e.Version).OriginalValue = request.Version;
            if (expense.Status == ExpenseStatus.Cancelled)
            {
                throw new AppException(StatusCodes.Status409Conflict, "already_cancelled", "این مصرف قبلاً لغو شده است.");
            }

            expense.Status = ExpenseStatus.Cancelled;
            await ledger.ReverseAsync("Expense", "Expense", id, ct);
            audit.Add("ExpenseCancelled", "Expense", id.ToString(), request.Reason);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        }).RequirePermission("expenses.cancel");
    }

    private static IQueryable<InvoiceRow> Project(IQueryable<Invoice> invoices) => invoices.Select(i => new InvoiceRow(
        i.Id, i.InvoiceNumber, i.PatientId, i.Patient != null ? i.Patient.FileNumber : null,
        i.Patient != null ? (i.Patient.FirstName + " " + (i.Patient.LastName ?? "")).Trim() : null,
        i.VisitId, i.Status.ToString(), i.Total, i.Discount, i.Paid, i.Refunded, i.Total - i.Discount - i.Paid, i.CreatedAt, i.Version));

    private static IQueryable<PaymentRow> QueryPayments(AppDbContext db, IQueryable<Payment> payments) =>
        from p in payments
        join u in db.Users on p.ReceivedByUserId equals u.Id
        select new PaymentRow(p.Id, p.ReceiptNumber, p.InvoiceId, p.Invoice!.InvoiceNumber, p.Amount, p.Method.ToString(), p.Reference, p.Refunded, p.CreatedAt, u.FullName);

    private static IQueryable<SessionRow> QuerySessions(AppDbContext db, IQueryable<CashSession> sessions) =>
        from s in sessions
        join u in db.Users on s.OpenedByUserId equals u.Id
        select new SessionRow(
            s.Id, s.RegisterId, s.Register!.Name, u.FullName, s.OpeningAmount, s.OpenedAt, s.ClosedAt, s.ExpectedAmount, s.CountedAmount,
            db.Payments.Where(p => p.CashSessionId == s.Id && p.Method == PaymentMethod.Cash).Sum(p => (decimal?)p.Amount) ?? 0,
            s.Version);
}

public sealed record SetPriceRequest([Range(0, 100000000)] decimal Price);
