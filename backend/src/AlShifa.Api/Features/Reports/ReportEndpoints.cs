using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Clinical;
using AlShifa.Api.Features.Finance;
using AlShifa.Api.Features.Visits;
using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AlShifa.Api.Features.Reports;

public sealed record CountRow(string Name, int Count);

public sealed record DoctorRow(string Doctor, int Visits, int Finalized);

public sealed record OperationsReport(
    DateOnly From, DateOnly To, int Visits, int CompletedVisits, IReadOnlyList<CountRow> VisitsByDepartment, IReadOnlyList<DoctorRow> Doctors,
    IReadOnlyList<CountRow> DiagnosticsByTest, int Surgeries, int Prescriptions, int OpticalOrders, int LowStockProducts);

public sealed record CashierRow(string Cashier, decimal Total);

public sealed record FinancialReport(
    DateOnly From, DateOnly To, decimal ServiceRevenue, decimal PharmacyRevenue, decimal OpticalRevenue, decimal Discounts, decimal Refunds,
    decimal NetRevenue, decimal CostOfGoods, decimal Expenses, decimal Receipts, IReadOnlyList<CashierRow> ReceiptsByCashier,
    decimal OutstandingNow, int OpenInvoicesNow, decimal StockValueNow);

public static class ReportEndpoints
{
    public static void MapReports(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/reports").WithTags("Reports");

        group.MapGet("/operations", async (DateOnly? from, DateOnly? to, AppDbContext db, CancellationToken ct) =>
        {
            var (start, end) = Range(from, to);
            var (startUtc, endUtc) = UtcRange(start, end);

            var visits = db.Visits.AsNoTracking().Where(v => v.VisitDate >= start && v.VisitDate <= end);

            var byDepartment = await visits.GroupBy(v => v.Department!.Name).Select(g => new CountRow(g.Key, g.Count())).ToListAsync(ct);

            var doctorVisits = await visits.Where(v => v.Doctor != null).GroupBy(v => v.Doctor!.FullName).Select(g => new { Doctor = g.Key, Count = g.Count() }).ToListAsync(ct);
            var finalized = await (
                from c in db.Consultations.AsNoTracking()
                join u in db.Users on c.AuthorUserId equals u.Id
                where c.FinalizedAt != null && c.FinalizedAt >= startUtc && c.FinalizedAt < endUtc
                group c by u.FullName into g
                select new { Doctor = g.Key, Count = g.Count() }).ToListAsync(ct);

            var doctors = doctorVisits.Select(d => d.Doctor).Union(finalized.Select(f => f.Doctor)).Order()
                .Select(name => new DoctorRow(name, doctorVisits.FirstOrDefault(d => d.Doctor == name)?.Count ?? 0, finalized.FirstOrDefault(f => f.Doctor == name)?.Count ?? 0))
                .ToList();

            var diagnostics = await db.DiagnosticOrders.AsNoTracking()
                .Where(o => o.CreatedAt >= startUtc && o.CreatedAt < endUtc && o.Status != DiagnosticStatus.Cancelled)
                .GroupBy(o => o.Test!.Name).Select(g => new CountRow(g.Key, g.Count())).ToListAsync(ct);

            var lowStock = await db.Products.AsNoTracking().CountAsync(p => p.IsActive && p.ReorderLevel > 0 && (db.StockBatches.Where(b => b.ProductId == p.Id).Sum(b => (decimal?)b.Quantity) ?? 0) <= p.ReorderLevel, ct);

            return Results.Ok(new OperationsReport(
                start, end,
                await visits.CountAsync(ct),
                await visits.CountAsync(v => v.Status == VisitStatus.Completed, ct),
                byDepartment.OrderByDescending(r => r.Count).ToList(), doctors, diagnostics.OrderByDescending(r => r.Count).ToList(),
                await db.SurgeryCases.AsNoTracking().CountAsync(c => c.CreatedAt >= startUtc && c.CreatedAt < endUtc && c.Status != SurgeryStatus.Cancelled, ct),
                await db.MedicalPrescriptions.AsNoTracking().CountAsync(p => p.CreatedAt >= startUtc && p.CreatedAt < endUtc && p.Status != PrescriptionStatus.Cancelled, ct),
                await db.OpticalOrders.AsNoTracking().CountAsync(o => o.CreatedAt >= startUtc && o.CreatedAt < endUtc, ct),
                lowStock));
        }).RequirePermission("reports.view");

        // Every figure comes from the ledger and source tables, so a report cannot disagree with the books.
        group.MapGet("/financial", async (DateOnly? from, DateOnly? to, AppDbContext db, CancellationToken ct) =>
        {
            var (start, end) = Range(from, to);
            var (startUtc, endUtc) = UtcRange(start, end);

            var movement = await (
                from e in db.LedgerEntries.AsNoTracking()
                join t in db.LedgerTransactions.AsNoTracking() on e.TransactionId equals t.Id
                where t.CreatedAt >= startUtc && t.CreatedAt < endUtc
                group e by e.AccountCode into g
                select new { Code = g.Key, Debit = g.Sum(x => x.Debit), Credit = g.Sum(x => x.Credit) }).ToListAsync(ct);

            decimal CreditNet(string code) => movement.Where(m => m.Code == code).Sum(m => m.Credit - m.Debit);
            decimal DebitNet(string code) => movement.Where(m => m.Code == code).Sum(m => m.Debit - m.Credit);

            var service = CreditNet(Accounts.ServiceRevenue);
            var pharmacy = CreditNet(Accounts.PharmacyRevenue);
            var optical = CreditNet(Accounts.OpticalRevenue);
            var discounts = DebitNet(Accounts.Discounts);
            var refunds = DebitNet(Accounts.Refunds);

            var byCashier = await (
                from p in db.Payments.AsNoTracking()
                join u in db.Users on p.ReceivedByUserId equals u.Id
                where p.CreatedAt >= startUtc && p.CreatedAt < endUtc
                group p by u.FullName into g
                select new CashierRow(g.Key, g.Sum(x => x.Amount))).ToListAsync(ct);

            var open = db.Invoices.AsNoTracking().Where(i => i.Status == InvoiceStatus.Issued || i.Status == InvoiceStatus.PartiallyPaid);

            return Results.Ok(new FinancialReport(
                start, end, service, pharmacy, optical, discounts, refunds,
                service + pharmacy + optical - discounts - refunds,
                DebitNet(Accounts.CostOfGoods),
                DebitNet(Accounts.Expenses),
                byCashier.Sum(c => c.Total),
                byCashier,
                await open.SumAsync(i => (decimal?)(i.Total - i.Discount - i.Paid), ct) ?? 0,
                await open.CountAsync(ct),
                await db.StockBatches.AsNoTracking().SumAsync(b => (decimal?)(b.Quantity * b.UnitCost), ct) ?? 0));
        }).RequirePermission("ledger.view");
    }

    private static (DateOnly Start, DateOnly End) Range(DateOnly? from, DateOnly? to)
    {
        var start = from ?? KabulClock.Today;
        var end = to ?? start;
        if (end < start)
        {
            throw new AppException(StatusCodes.Status400BadRequest, "invalid_range", "تاریخ پایان نمی‌تواند پیش از تاریخ شروع باشد.");
        }

        return (start, end);
    }

    /// <summary>Kabul is UTC+4:30 all year, so a Kabul calendar day starts at 19:30 UTC the day before.</summary>
    private static (DateTime Start, DateTime End) UtcRange(DateOnly start, DateOnly end) =>
        (start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddHours(-4.5), end.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddHours(-4.5));
}
