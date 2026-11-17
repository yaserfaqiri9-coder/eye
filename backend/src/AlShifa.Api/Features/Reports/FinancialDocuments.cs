using AlShifa.Api.Features.Finance;
using AlShifa.Api.Features.Clinical;
using AlShifa.Api.Features.Visits;
using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AlShifa.Api.Features.Reports;

public static partial class ReportDocuments
{
    private const string LedgerBasis = "بر اساس زمان ثبت سند به وقت کابل؛ اسناد معکوس نیز در دوره ثبت خود حساب می‌شوند.";

    private sealed record FinancialMovement(DateTime Day, string Code, decimal Debit, decimal Credit);

    private static Task<List<FinancialMovement>> FinancialMovementsAsync(Period p, AppDbContext db, CancellationToken ct) =>
        (from e in db.LedgerEntries.AsNoTracking()
         join t in db.LedgerTransactions.AsNoTracking() on e.TransactionId equals t.Id
         where t.CreatedAt >= p.StartUtc && t.CreatedAt < p.EndUtc
         group e by new { Day = t.CreatedAt.AddMinutes(270).Date, e.AccountCode } into g
         select new FinancialMovement(g.Key.Day, g.Key.AccountCode, g.Sum(e => e.Debit), g.Sum(e => e.Credit)))
        .ToListAsync(ct);

    private sealed class FinancialTotals(IEnumerable<FinancialMovement> movement)
    {
        private decimal Debit(string code) => movement.Where(m => m.Code == code).Sum(m => m.Debit - m.Credit);
        public decimal Gross => -Debit(Accounts.ServiceRevenue) - Debit(Accounts.PharmacyRevenue) - Debit(Accounts.OpticalRevenue);
        public decimal Discounts => Debit(Accounts.Discounts);
        public decimal Refunds => Debit(Accounts.Refunds);
        public decimal Net => Gross - Discounts - Refunds;
        public decimal Cost => Debit(Accounts.CostOfGoods);
        public decimal Expenses => Debit(Accounts.Expenses);
        public decimal Adjustments => Debit(Accounts.InventoryAdjustments);
        public decimal Profit => Net - Cost - Expenses - Adjustments;
    }

    private static async Task<ReportDocument> ProfitLossAsync(Period p, AppDbContext db, HttpContext http, CancellationToken ct)
    {
        var f = new FinancialTotals(await FinancialMovementsAsync(p, db, ct));
        return new("مفاد و ضرر کل سیستم",
            $"{LedgerBasis} مفاد ثبت‌شده = عواید خالص − قیمت خرید اجناس فروخته‌شده − مصارف − کمی و زیادی اجناس؛ دریافت پول به‌تنهایی عاید یا مفاد نیست.",
            p.Start, p.End,
            [MoneyTile("عواید خالص", f.Net), MoneyTile("قیمت خرید فروش‌ها", f.Cost), MoneyTile("مصارف و تعدیل گدام", f.Expenses + f.Adjustments), MoneyTile("مفاد / ضرر", f.Profit)], null,
            [new("محاسبه مفاد و ضرر", [Text("شرح"), Money("مبلغ (افغانی)")],
                [Row("عواید خدمات، دواخانه و عینک", f.Gross), Row("کسر: تخفیف‌ها", -f.Discounts), Row("کسر: برگشت وجه", -f.Refunds),
                 Row("عواید خالص", f.Net), Row("کسر: قیمت خرید اجناس فروخته‌شده", -f.Cost), Row("کسر: مصارف", -f.Expenses),
                 Row("کسر: کمی و زیادی اجناس", -f.Adjustments)], Row("مفاد / ضرر ثبت‌شده", f.Profit))]);
    }

    private static async Task<ReportDocument> RevenueSourcesAsync(Period p, AppDbContext db, HttpContext http, CancellationToken ct)
    {
        // Keep the charge and its reversal together, even when the original item is now void.
        // Non-service sales are classified by their posted revenue account, never by the collecting cashier.
        var sources = await (
            from e in db.LedgerEntries.AsNoTracking()
            join t in db.LedgerTransactions.AsNoTracking() on e.TransactionId equals t.Id
            join item in db.InvoiceItems.AsNoTracking() on new { t.SourceType, t.SourceId } equals new { SourceType = "InvoiceItem", SourceId = item.Id } into items
            from item in items.DefaultIfEmpty()
            join service in db.Services.AsNoTracking() on item.ServiceId equals (int?)service.Id into services
            from service in services.DefaultIfEmpty()
            join department in db.Departments.AsNoTracking() on service.DepartmentId equals (int?)department.Id into departments
            from department in departments.DefaultIfEmpty()
            join token in db.VisitTokens.AsNoTracking() on new { item.SourceType, item.SourceId } equals new { SourceType = nameof(VisitToken), SourceId = (long?)token.Id } into tokens
            from token in tokens.DefaultIfEmpty()
            join visit in db.Visits.AsNoTracking() on new { item.SourceType, item.SourceId } equals new { SourceType = nameof(Visit), SourceId = (long?)visit.Id } into visits
            from visit in visits.DefaultIfEmpty()
            join diagnostic in db.DiagnosticOrders.AsNoTracking() on new { item.SourceType, item.SourceId } equals new { SourceType = nameof(DiagnosticOrder), SourceId = (long?)diagnostic.Id } into diagnostics
            from diagnostic in diagnostics.DefaultIfEmpty()
            where t.CreatedAt >= p.StartUtc && t.CreatedAt < p.EndUtc &&
                (e.AccountCode == Accounts.ServiceRevenue || e.AccountCode == Accounts.PharmacyRevenue || e.AccountCode == Accounts.OpticalRevenue)
            group e by e.AccountCode == Accounts.PharmacyRevenue ? "دواخانه"
                : e.AccountCode == Accounts.OpticalRevenue ? "عینک"
                : item.SourceType == nameof(SurgeryCase) ? "عملیات"
                : diagnostic != null ? (diagnostic.Test!.Kind == DiagnosticKind.Lab ? "آزمایشگاه" : diagnostic.Test.Kind == DiagnosticKind.Refraction ? "رفرکشن" : "عکس‌برداری")
                : token != null ? token.Department!.Name
                : visit != null ? visit.Department!.Name
                : department.Name ?? "خدمات بدون بخش" into g
            select new { Name = g.Key, Amount = g.Sum(e => e.Credit - e.Debit) }).ToListAsync(ct);
        var f = new FinancialTotals(await FinancialMovementsAsync(p, db, ct));
        var rows = sources.OrderByDescending(s => s.Amount).ThenBy(s => s.Name).Select(s =>
            (IReadOnlyList<object?>)Row(s.Name, s.Amount)).ToList();
        // Discounts and refunds are posted at invoice/payment level and cannot reliably be assigned to a department.
        rows.Add(Row("تخفیف‌ها — مشترک بین بخش‌ها", -f.Discounts));
        rows.Add(Row("برگشت وجه — مشترک بین بخش‌ها", -f.Refunds));
        return new("عواید بخش‌ها", $"{LedgerBasis} تخفیف و برگشت وجه به‌صورت مشترک آمده‌اند؛ سهم یا مفاد هر بخش از روی آن‌ها تخمین زده نمی‌شود.",
            p.Start, p.End,
            [MoneyTile("عواید پیش از کسورات", f.Gross), MoneyTile("تخفیف‌ها", f.Discounts), MoneyTile("برگشت وجه", f.Refunds), MoneyTile("عواید خالص", f.Net)], null,
            [new("عواید به تفکیک بخش", [Text("بخش / کسورات"), Money("مبلغ (افغانی)")], rows, Row("عواید خالص", f.Net))]);
    }

    private static async Task<ReportDocument> ExpenseSummaryAsync(Period p, AppDbContext db, HttpContext http, CancellationToken ct)
    {
        var categories = await (
            from e in db.LedgerEntries.AsNoTracking()
            join t in db.LedgerTransactions.AsNoTracking() on e.TransactionId equals t.Id
            join expense in db.Expenses.AsNoTracking() on new { t.SourceType, t.SourceId } equals new { SourceType = "Expense", SourceId = expense.Id } into expenses
            from expense in expenses.DefaultIfEmpty()
            where t.CreatedAt >= p.StartUtc && t.CreatedAt < p.EndUtc && e.AccountCode == Accounts.Expenses
            group e by expense.Category ?? "مصارف بدون نوع" into g
            select new { Category = g.Key, Debit = g.Sum(e => e.Debit), Credit = g.Sum(e => e.Credit) })
            .OrderByDescending(g => g.Debit - g.Credit).ThenBy(g => g.Category).ToListAsync(ct);
        var debit = categories.Sum(c => c.Debit);
        var credit = categories.Sum(c => c.Credit);
        return new("خلاصه مصارف به تفکیک نوع", $"{LedgerBasis} تاریخ مصرف واردشده ممکن است متفاوت باشد. لغو مصرف در ستون برگشت سند کم می‌شود؛ خرید موجودی، مصرف عملیاتی محسوب نمی‌شود.",
            p.Start, p.End,
            [CountTile("انواع مصرف", categories.Count), MoneyTile("مصارف ثبت‌شده", debit), MoneyTile("برگشت اسناد مصرف", credit), MoneyTile("مصارف خالص", debit - credit)], null,
            [new("مصارف به تفکیک نوع", [Text("نوع مصرف"), Money("ثبت‌شده"), Money("برگشت سند"), Money("خالص (افغانی)")],
                categories.Select(c => (IReadOnlyList<object?>)Row(c.Category, c.Debit, c.Credit, c.Debit - c.Credit)).ToList(), Row("مجموع", debit, credit, debit - credit))]);
    }

    private static async Task<ReportDocument> DailyFinanceAsync(Period p, AppDbContext db, HttpContext http, CancellationToken ct)
    {
        var movements = await FinancialMovementsAsync(p, db, ct);
        var f = new FinancialTotals(movements);
        var days = movements.GroupBy(m => m.Day).OrderBy(g => g.Key).ToList();
        var rows = days.Take(MaxListRows).Select(day =>
        {
            var d = new FinancialTotals(day);
            return (IReadOnlyList<object?>)Row(DateOnly.FromDateTime(day.Key), d.Net, d.Cost, d.Expenses, d.Adjustments, d.Profit);
        }).ToList();
        return new("عملکرد مالی روزانه", $"{LedgerBasis} فقط روزهای دارای سند نشان داده می‌شوند؛ مجموع‌ها تمام دوره را پوشش می‌دهند.", p.Start, p.End,
            [MoneyTile("عواید خالص", f.Net), MoneyTile("قیمت خرید فروش‌ها", f.Cost), MoneyTile("مصارف و تعدیل گدام", f.Expenses + f.Adjustments), MoneyTile("مفاد / ضرر", f.Profit)], null,
            [new("عواید، مصارف و مفاد روزانه", [Date("روز"), Money("عواید خالص"), Money("قیمت خرید فروش‌ها"), Money("مصارف"), Money("تعدیل گدام"), Money("مفاد / ضرر")],
                rows, Row("مجموع دوره", f.Net, f.Cost, f.Expenses, f.Adjustments, f.Profit), Cut(rows.Count, days.Count, "روز"))]);
    }

    private static async Task<ReportDocument> CashFlowAsync(Period p, AppDbContext db, HttpContext http, CancellationToken ct)
    {
        var opening = await (from e in db.LedgerEntries.AsNoTracking()
                             join t in db.LedgerTransactions.AsNoTracking() on e.TransactionId equals t.Id
                             where e.AccountCode == Accounts.Cash && t.CreatedAt < p.StartUtc
                             select (decimal?)(e.Debit - e.Credit)).SumAsync(ct) ?? 0;
        var movement = await (from e in db.LedgerEntries.AsNoTracking()
                              join t in db.LedgerTransactions.AsNoTracking() on e.TransactionId equals t.Id
                              where e.AccountCode == Accounts.Cash && t.CreatedAt >= p.StartUtc && t.CreatedAt < p.EndUtc
                              group e by t.CreatedAt.AddMinutes(270).Date into g
                              select new { Day = g.Key, In = g.Sum(e => e.Debit), Out = g.Sum(e => e.Credit) }).OrderBy(g => g.Day).ToListAsync(ct);
        var balance = opening;
        var rows = movement.Take(MaxListRows).Select(d =>
        {
            balance += d.In - d.Out;
            return (IReadOnlyList<object?>)Row(DateOnly.FromDateTime(d.Day), d.In, d.Out, d.In - d.Out, balance);
        }).ToList();
        var incoming = movement.Sum(d => d.In);
        var outgoing = movement.Sum(d => d.Out);
        var closing = opening + incoming - outgoing;
        return new("گردش حساب صندوق", $"{LedgerBasis} مانده دفتری حساب صندوق است؛ شامل ثبت‌های غیرنقدی همین حساب نیز می‌شود. پول آغاز جلسات و شمارش واقعی صندوق در این مانده نیست.", p.Start, p.End,
            [MoneyTile("مانده دفتری آغاز", opening), MoneyTile("ورودی حساب", incoming), MoneyTile("خروجی حساب", outgoing), MoneyTile("مانده دفتری پایان", closing)], null,
            [new("گردش روزانه حساب صندوق", [Date("روز"), Money("ورودی"), Money("خروجی"), Money("تغییر خالص"), Money("مانده دفتری")], rows,
                Row("مجموع دوره / مانده پایان", incoming, outgoing, incoming - outgoing, closing), Cut(rows.Count, movement.Count, "روز"))]);
    }
}
