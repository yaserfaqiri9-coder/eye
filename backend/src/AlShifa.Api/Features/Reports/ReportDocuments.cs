using System.Globalization;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Clinical;
using AlShifa.Api.Features.Finance;
using AlShifa.Api.Features.Inventory;
using AlShifa.Api.Features.Patients;
using AlShifa.Api.Features.Visits;
using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AlShifa.Api.Features.Reports;

/// <summary>How the reports page shows a value: count, money, quantity, text, date or datetime.</summary>
public static class ReportKind
{
    public const string Count = "count";
    public const string Money = "money";
    public const string Quantity = "quantity";
    public const string Text = "text";
    public const string Date = "date";
    public const string DateTime = "datetime";
}

public sealed record ReportTile(string Label, decimal Value, string Kind);

public sealed record ReportColumn(string Label, string Kind);

/// <param name="Note">A plain sentence for anything a reader could otherwise misread, such as a list cut short.</param>
public sealed record ReportTable(string Title, IReadOnlyList<ReportColumn> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows, IReadOnlyList<object?>? Total = null, string? Note = null);

public sealed record ReportPoint(DateOnly Date, decimal Value);

public sealed record ReportTrend(string Title, string Kind, IReadOnlyList<ReportPoint> Points);

/// <summary>
/// One report in the single shape the page renders, prints and exports. From and To are null on a report of the present
/// moment. Reports for clinical and front-desk staff hold counts only, with no patient named.
/// </summary>
public sealed record ReportDocument(string Title, string Description, DateOnly? From, DateOnly? To, IReadOnlyList<ReportTile> Tiles, ReportTrend? Trend, IReadOnlyList<ReportTable> Tables);

public static class ReportDocuments
{
    /// <summary>Longest period that still lists day by day; beyond it a daily list is too long to read or print.</summary>
    private const int MaxDailyDays = 62;

    private const int MaxListRows = 500;

    private const int NearExpiryDays = 90;

    private sealed record Period(DateOnly Start, DateOnly End, DateTime StartUtc, DateTime EndUtc)
    {
        public int Days => End.DayNumber - Start.DayNumber + 1;
    }

    private static Period Of(DateOnly? from, DateOnly? to)
    {
        var (start, end) = ReportEndpoints.Range(from, to);
        var (startUtc, endUtc) = ReportEndpoints.UtcRange(start, end);
        return new Period(start, end, startUtc, endUtc);
    }

    public static void MapReportDocuments(this IEndpointRouteBuilder group)
    {
        group.MapGet("/overview", async (DateOnly? from, DateOnly? to, AppDbContext db, HttpContext http, CancellationToken ct) =>
            Results.Ok(await OverviewAsync(Of(from, to), db, http, ct))).RequirePermission("reports.view");
        group.MapGet("/patients", async (DateOnly? from, DateOnly? to, AppDbContext db, CancellationToken ct) =>
            Results.Ok(await PatientsAsync(Of(from, to), db, ct))).RequirePermission("reports.view");
        group.MapGet("/doctors", async (DateOnly? from, DateOnly? to, AppDbContext db, CancellationToken ct) =>
            Results.Ok(await DoctorsAsync(Of(from, to), db, ct))).RequirePermission("reports.view");
        group.MapGet("/diagnostics", async (DateOnly? from, DateOnly? to, AppDbContext db, CancellationToken ct) =>
            Results.Ok(await DiagnosticsAsync(Of(from, to), db, ct))).RequirePermission("reports.view");
        group.MapGet("/surgeries", async (DateOnly? from, DateOnly? to, AppDbContext db, CancellationToken ct) =>
            Results.Ok(await SurgeriesAsync(Of(from, to), db, ct))).RequirePermission("reports.view");
        group.MapGet("/diseases", async (DateOnly? from, DateOnly? to, AppDbContext db, CancellationToken ct) =>
            Results.Ok(await DiseasesAsync(Of(from, to), db, ct))).RequirePermission("reports.view");

        group.MapGet("/income", async (DateOnly? from, DateOnly? to, AppDbContext db, CancellationToken ct) =>
            Results.Ok(await IncomeAsync(Of(from, to), db, ct))).RequirePermission("ledger.view");
        group.MapGet("/cash", async (DateOnly? from, DateOnly? to, AppDbContext db, CancellationToken ct) =>
            Results.Ok(await CashAsync(Of(from, to), db, ct))).RequirePermission("ledger.view");
        group.MapGet("/outstanding", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok(await OutstandingAsync(db, ct))).RequirePermission("ledger.view");
        group.MapGet("/adjustments", async (DateOnly? from, DateOnly? to, AppDbContext db, CancellationToken ct) =>
            Results.Ok(await AdjustmentsAsync(Of(from, to), db, ct))).RequirePermission("ledger.view");
        group.MapGet("/expenses", async (DateOnly? from, DateOnly? to, AppDbContext db, CancellationToken ct) =>
            Results.Ok(await ExpensesAsync(Of(from, to), db, ct))).RequirePermission("ledger.view");

        // Each department sees its own sales: the page asks once and the answer holds only what this user may see.
        group.MapGet("/sales", async (DateOnly? from, DateOnly? to, AppDbContext db, HttpContext http, CancellationToken ct) =>
            Results.Ok(await SalesAsync(Of(from, to), db, http, ct))).RequirePermission("reports.view");
        group.MapGet("/stock", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok(await StockAsync(db, ct))).RequirePermission("inventory.view");
    }

    private static ReportColumn Text(string label) => new(label, ReportKind.Text);

    private static ReportColumn Count(string label) => new(label, ReportKind.Count);

    private static ReportColumn Money(string label) => new(label, ReportKind.Money);

    private static ReportTile CountTile(string label, decimal value) => new(label, value, ReportKind.Count);

    private static ReportTile MoneyTile(string label, decimal value) => new(label, value, ReportKind.Money);

    private static object?[] Row(params object?[] cells) => cells;

    /// <summary>A visit the patient actually came for: its fee is settled and it was not cancelled.</summary>
    private static IQueryable<Visit> Attended(AppDbContext db, Period p) =>
        db.Visits.AsNoTracking().Where(v => v.VisitDate >= p.Start && v.VisitDate <= p.End && v.Status != VisitStatus.AwaitingPayment && v.Status != VisitStatus.Cancelled);

    private static ReportTrend? DailyTrend(string title, string kind, Period p, IReadOnlyDictionary<DateOnly, decimal> values)
    {
        if (p.Days < 2 || p.Days > MaxDailyDays)
        {
            return null;
        }

        var points = Enumerable.Range(0, p.Days).Select(offset => p.Start.AddDays(offset))
            .Select(day => new ReportPoint(day, values.GetValueOrDefault(day))).ToList();
        return new ReportTrend(title, kind, points);
    }

    private static async Task<List<ReportCount>> ByDepartmentAsync(AppDbContext db, Period p, CancellationToken ct)
    {
        var rows = await Attended(db, p).GroupBy(v => v.Department!.Name).Select(g => new ReportCount(g.Key, g.Count())).ToListAsync(ct);
        return rows.OrderByDescending(r => r.Count).ToList();
    }

    private sealed record ReportCount(string Name, int Count);

    private static ReportTable CountTable(string title, string nameLabel, string countLabel, IReadOnlyCollection<ReportCount> rows, string? note = null) =>
        new(title, [Text(nameLabel), Count(countLabel)], rows.Select(r => Row(r.Name, r.Count)).ToList(), rows.Count > 1 ? Row("مجموع", rows.Sum(r => r.Count)) : null, note);

    private static async Task<ReportDocument> OverviewAsync(Period p, AppDbContext db, HttpContext http, CancellationToken ct)
    {
        var attended = Attended(db, p);
        var perDay = await attended.GroupBy(v => v.VisitDate).Select(g => new { Day = g.Key, Count = g.Count() }).ToListAsync(ct);
        var tiles = new List<ReportTile>
        {
            CountTile("مراجعات", perDay.Sum(d => d.Count)),
            CountTile("مریضان جدید", await db.Patients.AsNoTracking().CountAsync(x => x.CreatedAt >= p.StartUtc && x.CreatedAt < p.EndUtc, ct)),
            CountTile("مراجعات تکمیل‌شده", await attended.CountAsync(v => v.Status == VisitStatus.Completed, ct)),
            CountTile("عملیات", await db.SurgeryCases.AsNoTracking().CountAsync(c => c.CreatedAt >= p.StartUtc && c.CreatedAt < p.EndUtc && c.Status != SurgeryStatus.Cancelled, ct)),
        };

        if (http.User.HasClaim(RolePermissions.ClaimType, "ledger.view"))
        {
            var financial = await ReportEndpoints.LoadFinancialAsync(p.Start, p.End, db, ct);
            tiles.Add(MoneyTile("پول دریافت‌شده", financial.Receipts));
            tiles.Add(MoneyTile("مصارف", financial.Expenses));
        }

        return new ReportDocument(
            "خلاصه کار", "نمای کلی کار شفاخانه در دوره انتخاب‌شده.", p.Start, p.End, tiles,
            DailyTrend("مراجعات هر روز", ReportKind.Count, p, perDay.ToDictionary(d => d.Day, d => (decimal)d.Count)),
            [CountTable("مراجعات به تفکیک بخش", "بخش", "مراجعات", await ByDepartmentAsync(db, p, ct))]);
    }

    private static async Task<ReportDocument> PatientsAsync(Period p, AppDbContext db, CancellationToken ct)
    {
        var attended = Attended(db, p);
        var perDay = await attended.GroupBy(v => v.VisitDate).Select(g => new { Day = g.Key, Count = g.Count(), Completed = g.Count(v => v.Status == VisitStatus.Completed) }).ToListAsync(ct);
        var cancelledPerDay = await db.Visits.AsNoTracking()
            .Where(v => v.VisitDate >= p.Start && v.VisitDate <= p.End && v.Status == VisitStatus.Cancelled)
            .GroupBy(v => v.VisitDate).Select(g => new { Day = g.Key, Count = g.Count() }).ToListAsync(ct);

        // One person counts once, however many times they came in the period.
        var people = await attended.Select(v => v.Patient!).Distinct()
            .Select(x => new { x.Gender, x.BirthDate, x.EstimatedBirthYear }).ToListAsync(ct);
        var male = people.Count(x => x.Gender == Gender.Male);
        var female = people.Count(x => x.Gender == Gender.Female);

        var ages = people.Select(x => PatientEndpoints.AgeOf(x.BirthDate, x.EstimatedBirthYear)).ToList();
        ReportCount Band(string name, int from, int to) => new(name, ages.Count(a => a >= from && a <= to));
        var ageRows = new List<ReportCount> { Band("کمتر از ۱۵ سال", 0, 14), Band("۱۵ تا ۳۹ سال", 15, 39), Band("۴۰ تا ۵۹ سال", 40, 59), Band("۶۰ سال و بالاتر", 60, 200), new("سن ثبت نشده", ages.Count(a => a is null)) };

        var genderRows = new List<ReportCount> { new("مرد", male), new("زن", female) };
        var otherGender = people.Count - male - female;
        if (otherGender > 0)
        {
            genderRows.Add(new("ثبت نشده", otherGender));
        }

        var appointments = await db.Appointments.AsNoTracking()
            .Where(a => a.ScheduledAt >= p.StartUtc && a.ScheduledAt < p.EndUtc)
            .GroupBy(a => a.Status).Select(g => new { Status = g.Key, Count = g.Count() }).ToListAsync(ct);
        int Appointments(AppointmentStatus status) => appointments.FirstOrDefault(a => a.Status == status)?.Count ?? 0;

        var tables = new List<ReportTable>
        {
            CountTable("مراجعات به تفکیک بخش", "بخش", "مراجعات", await ByDepartmentAsync(db, p, ct)),
            CountTable("مریضان به تفکیک جنسیت", "جنسیت", "مریض", genderRows),
            CountTable("مریضان به تفکیک سن", "گروه سنی", "مریض", ageRows),
            CountTable("وقت‌های ملاقات", "وضعیت", "تعداد",
            [
                new("آمدند", Appointments(AppointmentStatus.CheckedIn)),
                new("هنوز در انتظار", Appointments(AppointmentStatus.Scheduled)),
                new("نیامدند", Appointments(AppointmentStatus.NoShow)),
                new("لغو شده", Appointments(AppointmentStatus.Cancelled)),
            ]),
        };

        if (p.Days >= 2 && p.Days <= MaxDailyDays)
        {
            var rows = Enumerable.Range(0, p.Days).Select(offset => p.Start.AddDays(offset)).Select(day =>
            {
                var row = perDay.FirstOrDefault(d => d.Day == day);
                return Row(day, row?.Count ?? 0, row?.Completed ?? 0, cancelledPerDay.FirstOrDefault(d => d.Day == day)?.Count ?? 0);
            }).ToList();
            tables.Add(new ReportTable("مراجعات روز به روز", [new("تاریخ", ReportKind.Date), Count("مراجعات"), Count("تکمیل‌شده"), Count("لغو شده")], rows,
                Row("مجموع", perDay.Sum(d => d.Count), perDay.Sum(d => d.Completed), cancelledPerDay.Sum(d => d.Count))));
        }

        return new ReportDocument(
            "مریضان و مراجعات", "چند مریض آمدند، به کدام بخش رفتند و از چه گروهی بودند.", p.Start, p.End,
            [
                CountTile("مراجعات", perDay.Sum(d => d.Count)),
                CountTile("تعداد مریضان", people.Count),
                CountTile("مریضان جدید", await db.Patients.AsNoTracking().CountAsync(x => x.CreatedAt >= p.StartUtc && x.CreatedAt < p.EndUtc, ct)),
                CountTile("مراجعات لغو شده", cancelledPerDay.Sum(d => d.Count)),
            ],
            DailyTrend("مراجعات هر روز", ReportKind.Count, p, perDay.ToDictionary(d => d.Day, d => (decimal)d.Count)),
            tables);
    }

    private static async Task<ReportDocument> DoctorsAsync(Period p, AppDbContext db, CancellationToken ct)
    {
        var visits = await Attended(db, p).Where(v => v.Doctor != null).GroupBy(v => v.Doctor!.FullName)
            .Select(g => new ReportCount(g.Key, g.Count())).ToListAsync(ct);
        var finalized = await (
            from c in db.Consultations.AsNoTracking()
            join u in db.Users on c.AuthorUserId equals u.Id
            where c.FinalizedAt != null && c.FinalizedAt >= p.StartUtc && c.FinalizedAt < p.EndUtc
            group c by u.FullName into g
            select new ReportCount(g.Key, g.Count())).ToListAsync(ct);
        var prescriptions = await (
            from x in db.MedicalPrescriptions.AsNoTracking()
            join u in db.Users on x.DoctorUserId equals u.Id
            where x.CreatedAt >= p.StartUtc && x.CreatedAt < p.EndUtc && x.Status != PrescriptionStatus.Cancelled
            group x by u.FullName into g
            select new ReportCount(g.Key, g.Count())).ToListAsync(ct);
        var surgeries = await (
            from x in db.SurgeryCases.AsNoTracking()
            join u in db.Users on x.ResponsibleUserId equals u.Id
            where x.CreatedAt >= p.StartUtc && x.CreatedAt < p.EndUtc && x.Status != SurgeryStatus.Cancelled
            group x by u.FullName into g
            select new ReportCount(g.Key, g.Count())).ToListAsync(ct);

        static int Of(List<ReportCount> rows, string name) => rows.FirstOrDefault(r => r.Name == name)?.Count ?? 0;
        var names = visits.Concat(finalized).Concat(prescriptions).Concat(surgeries).Select(r => r.Name).Distinct()
            .OrderByDescending(name => Of(visits, name)).ThenBy(name => name).ToList();

        return new ReportDocument(
            "کار داکتران", "هر داکتر در این دوره چند مریض دید و چه کارهایی ثبت کرد.", p.Start, p.End,
            [
                CountTile("داکتران فعال", names.Count),
                CountTile("مراجعات نزد داکتر", visits.Sum(r => r.Count)),
                CountTile("معاینات نهایی‌شده", finalized.Sum(r => r.Count)),
                CountTile("نسخه‌های دوا", prescriptions.Sum(r => r.Count)),
            ],
            null,
            [
                new ReportTable("کار هر داکتر", [Text("داکتر"), Count("مراجعات"), Count("معاینات نهایی‌شده"), Count("نسخه دوا"), Count("عملیات")],
                    names.Select(name => Row(name, Of(visits, name), Of(finalized, name), Of(prescriptions, name), Of(surgeries, name))).ToList(),
                    names.Count > 1 ? Row("مجموع", visits.Sum(r => r.Count), finalized.Sum(r => r.Count), prescriptions.Sum(r => r.Count), surgeries.Sum(r => r.Count)) : null),
            ]);
    }

    private static string KindName(DiagnosticKind kind) => kind switch
    {
        DiagnosticKind.Lab => "آزمایشگاه",
        DiagnosticKind.Refraction => "رفرکشن",
        _ => "عکس‌برداری",
    };

    private static async Task<ReportDocument> DiagnosticsAsync(Period p, AppDbContext db, CancellationToken ct)
    {
        var rows = await db.DiagnosticOrders.AsNoTracking()
            .Where(o => o.CreatedAt >= p.StartUtc && o.CreatedAt < p.EndUtc)
            .GroupBy(o => new { o.Test!.Name, o.Test.Kind, o.Status })
            .Select(g => new { g.Key.Name, g.Key.Kind, g.Key.Status, Count = g.Count() })
            .ToListAsync(ct);

        int Sum(IEnumerable<int> counts) => counts.Sum();
        int Done(DiagnosticKind? kind = null) => Sum(rows.Where(r => r.Status == DiagnosticStatus.ResultReady && (kind is null || r.Kind == kind)).Select(r => r.Count));
        int Requested(DiagnosticKind? kind = null) => Sum(rows.Where(r => r.Status != DiagnosticStatus.Cancelled && (kind is null || r.Kind == kind)).Select(r => r.Count));

        var tests = rows.GroupBy(r => new { r.Name, r.Kind })
            .Select(g => new
            {
                g.Key.Name,
                g.Key.Kind,
                Requested = g.Where(r => r.Status != DiagnosticStatus.Cancelled).Sum(r => r.Count),
                Done = g.Where(r => r.Status == DiagnosticStatus.ResultReady).Sum(r => r.Count),
                Open = g.Where(r => r.Status is DiagnosticStatus.Requested or DiagnosticStatus.InProgress).Sum(r => r.Count),
                Cancelled = g.Where(r => r.Status == DiagnosticStatus.Cancelled).Sum(r => r.Count),
            })
            .OrderByDescending(r => r.Requested).ThenBy(r => r.Name).ToList();

        return new ReportDocument(
            "آزمایش، رفرکشن و عکس‌برداری", "چند درخواست آمد، چند تا انجام شد و چند تا هنوز باز است.", p.Start, p.End,
            [
                CountTile("همه درخواست‌ها", Requested()),
                CountTile("انجام‌شده", Done()),
                CountTile("هنوز باز", tests.Sum(t => t.Open)),
                CountTile("لغو شده", tests.Sum(t => t.Cancelled)),
            ],
            null,
            [
                new ReportTable("به تفکیک خدمت", [Text("خدمت"), Text("بخش"), Count("درخواست"), Count("انجام‌شده"), Count("هنوز باز"), Count("لغو شده")],
                    tests.Select(t => Row(t.Name, KindName(t.Kind), t.Requested, t.Done, t.Open, t.Cancelled)).ToList(),
                    tests.Count > 1 ? Row("مجموع", null, tests.Sum(t => t.Requested), tests.Sum(t => t.Done), tests.Sum(t => t.Open), tests.Sum(t => t.Cancelled)) : null),
                new ReportTable("به تفکیک بخش", [Text("بخش"), Count("درخواست"), Count("انجام‌شده")],
                    Enum.GetValues<DiagnosticKind>().Select(k => Row(KindName(k), Requested(k), Done(k))).ToList()),
            ]);
    }

    private static async Task<ReportDocument> SurgeriesAsync(Period p, AppDbContext db, CancellationToken ct)
    {
        var rows = await (
            from s in db.SurgeryCases.AsNoTracking()
            join u in db.Users on s.ResponsibleUserId equals u.Id
            where s.CreatedAt >= p.StartUtc && s.CreatedAt < p.EndUtc
            group s by new { s.SurgeryType, Doctor = u.FullName, s.Status } into g
            select new { g.Key.SurgeryType, g.Key.Doctor, g.Key.Status, Count = g.Count() }).ToListAsync(ct);

        static bool IsDone(SurgeryStatus status) => status is SurgeryStatus.Completed or SurgeryStatus.FollowUp or SurgeryStatus.Closed;
        static bool IsOpen(SurgeryStatus status) => status is SurgeryStatus.Proposed or SurgeryStatus.Scheduled or SurgeryStatus.InProgress;

        var doneTotal = rows.Where(r => IsDone(r.Status)).Sum(r => r.Count);
        var openTotal = rows.Where(r => IsOpen(r.Status)).Sum(r => r.Count);
        var cancelledTotal = rows.Where(r => r.Status == SurgeryStatus.Cancelled).Sum(r => r.Count);

        static List<object?[]> By(IEnumerable<IGrouping<string, (SurgeryStatus Status, int Count)>> groups) =>
            groups.OrderByDescending(g => g.Where(r => r.Status != SurgeryStatus.Cancelled).Sum(r => r.Count)).ThenBy(g => g.Key)
                .Select(g => Row(g.Key,
                    g.Where(r => IsDone(r.Status)).Sum(r => r.Count),
                    g.Where(r => IsOpen(r.Status)).Sum(r => r.Count),
                    g.Where(r => r.Status == SurgeryStatus.Cancelled).Sum(r => r.Count))).ToList();

        var byType = By(rows.GroupBy(r => r.SurgeryType, r => (r.Status, r.Count)));
        var byDoctor = By(rows.GroupBy(r => r.Doctor, r => (r.Status, r.Count)));
        var total = Row("مجموع", doneTotal, openTotal, cancelledTotal);

        return new ReportDocument(
            "عملیات", "چند عملیات انجام شد و چند تا هنوز در انتظار است.", p.Start, p.End,
            [
                CountTile("همه عملیات‌ها", doneTotal + openTotal),
                CountTile("انجام‌شده", doneTotal),
                CountTile("در انتظار", openTotal),
                CountTile("لغو شده", cancelledTotal),
            ],
            null,
            [
                new ReportTable("به تفکیک نوع عملیات", [Text("نوع عملیات"), Count("انجام‌شده"), Count("در انتظار"), Count("لغو شده")], byType, byType.Count > 1 ? total : null),
                new ReportTable("به تفکیک داکتر", [Text("داکتر"), Count("انجام‌شده"), Count("در انتظار"), Count("لغو شده")], byDoctor, byDoctor.Count > 1 ? total : null),
            ]);
    }

    private static async Task<ReportDocument> DiseasesAsync(Period p, AppDbContext db, CancellationToken ct)
    {
        var finalized = db.Diagnoses.AsNoTracking().Where(d => d.Consultation!.FinalizedAt != null && d.Consultation.FinalizedAt >= p.StartUtc && d.Consultation.FinalizedAt < p.EndUtc);
        var rows = await finalized.GroupBy(d => d.Text)
            .Select(g => new { Name = g.Key, Count = g.Count(), Right = g.Count(d => d.Eye == EyeSide.OD), Left = g.Count(d => d.Eye == EyeSide.OS), Both = g.Count(d => d.Eye == EyeSide.Both) })
            .OrderByDescending(r => r.Count).ThenBy(r => r.Name).ToListAsync(ct);
        var shown = rows.Take(50).ToList();

        return new ReportDocument(
            "بیماری‌های شایع", "کدام تشخیص‌ها بیشتر ثبت شده‌اند. فقط شمارش است و نام هیچ مریضی در آن نیست.", p.Start, p.End,
            [
                CountTile("تشخیص‌های ثبت‌شده", rows.Sum(r => r.Count)),
                CountTile("نوع بیماری", rows.Count),
                CountTile("مریضان", await finalized.Select(d => d.Consultation!.Visit!.PatientId).Distinct().CountAsync(ct)),
            ],
            null,
            [
                new ReportTable("تشخیص‌ها از بیشتر به کمتر", [Text("تشخیص"), Count("تعداد"), Count("چشم راست"), Count("چشم چپ"), Count("هر دو چشم")],
                    shown.Select(r => Row(r.Name, r.Count, r.Right, r.Left, r.Both)).ToList(),
                    Note: "تشخیص‌ها همان متنی است که داکتر نوشته است، پس یک بیماری با املای متفاوت جدا شمرده می‌شود. فقط معاینات نهایی‌شده حساب شده‌اند." + (rows.Count > shown.Count ? $" فقط {shown.Count} تشخیص اول نشان داده شده است." : "")),
            ]);
    }

    private static async Task<ReportDocument> IncomeAsync(Period p, AppDbContext db, CancellationToken ct)
    {
        var f = await ReportEndpoints.LoadFinancialAsync(p.Start, p.End, db, ct);
        var result = f.NetRevenue - f.CostOfGoods - f.Expenses - f.InventoryAdjustments;

        // From the same ledger entries as the totals, so the lines always add up to them.
        var lines = await (
            from e in db.LedgerEntries.AsNoTracking()
            join t in db.LedgerTransactions.AsNoTracking() on e.TransactionId equals t.Id
            join i in db.InvoiceItems.AsNoTracking() on t.SourceId equals i.Id
            where t.SourceType == "InvoiceItem" && t.CreatedAt >= p.StartUtc && t.CreatedAt < p.EndUtc
                && (e.AccountCode == Accounts.ServiceRevenue || e.AccountCode == Accounts.PharmacyRevenue || e.AccountCode == Accounts.OpticalRevenue)
            group e by new { e.AccountCode, i.Description } into g
            select new { g.Key.AccountCode, g.Key.Description, Amount = g.Sum(x => x.Credit - x.Debit) }).ToListAsync(ct);

        var sources = lines.GroupBy(l => l.AccountCode switch
            {
                Accounts.PharmacyRevenue => "فروش دواخانه",
                Accounts.OpticalRevenue => "فروش عینک",
                _ => l.Description,
            })
            .Select(g => new { Name = g.Key, Amount = g.Sum(l => l.Amount) })
            .Where(r => r.Amount != 0)
            .OrderByDescending(r => r.Amount).ToList();

        var perDay = await (
            from x in db.Payments.AsNoTracking()
            where x.CreatedAt >= p.StartUtc && x.CreatedAt < p.EndUtc
            select new { x.CreatedAt, x.Amount }).ToListAsync(ct);
        var receiptsPerDay = perDay.GroupBy(x => DateOnly.FromDateTime(x.CreatedAt.AddHours(4.5))).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));

        return new ReportDocument(
            "درآمد و مصارف", "چقدر درآمد شد، از کجا آمد، چقدر مصرف شد و نتیجه چه بود.", p.Start, p.End,
            [
                MoneyTile("پول دریافت‌شده", f.Receipts),
                MoneyTile("درآمد خالص", f.NetRevenue),
                MoneyTile("مصارف", f.Expenses),
                MoneyTile("نتیجه دوره", result),
            ],
            DailyTrend("پول دریافت‌شده هر روز", ReportKind.Money, p, receiptsPerDay),
            [
                new ReportTable("حساب دوره", [Text("شرح"), Money("مبلغ")],
                [
                    Row("درآمد خدمات", f.ServiceRevenue),
                    Row("فروش دواخانه", f.PharmacyRevenue),
                    Row("فروش عینک", f.OpticalRevenue),
                    Row("کم: تخفیف‌ها", -f.Discounts),
                    Row("کم: برگشت وجه", -f.Refunds),
                    Row("درآمد خالص", f.NetRevenue),
                    Row("کم: قیمت خرید جنس فروخته‌شده", -f.CostOfGoods),
                    Row("کم: مصارف", -f.Expenses),
                    Row("کم: تعدیل موجودی", -f.InventoryAdjustments),
                ], Row("نتیجه دوره", result),
                "درآمد در روز ثبت فاکتور حساب می‌شود، حتی اگر پول آن هنوز گرفته نشده باشد. برای همین «درآمد خالص» و «پول دریافت‌شده» می‌توانند فرق داشته باشند."),
                new ReportTable("درآمد به تفکیک خدمت", [Text("خدمت"), Money("مبلغ")], sources.Select(r => Row(r.Name, r.Amount)).ToList(),
                    sources.Count > 1 ? Row("مجموع", sources.Sum(r => r.Amount)) : null, "پیش از کم کردن تخفیف و برگشت وجه."),
            ]);
    }

    private static async Task<ReportDocument> CashAsync(Period p, AppDbContext db, CancellationToken ct)
    {
        var sessions = await FinanceEndpoints.QuerySessions(db, db.CashSessions.AsNoTracking().Where(s => s.OpenedAt >= p.StartUtc && s.OpenedAt < p.EndUtc).OrderByDescending(s => s.Id).Take(MaxListRows)).ToListAsync(ct);
        var payments = await (
            from x in db.Payments.AsNoTracking()
            join u in db.Users on x.ReceivedByUserId equals u.Id
            where x.CreatedAt >= p.StartUtc && x.CreatedAt < p.EndUtc
            group x by new { u.FullName, x.Method } into g
            select new { Cashier = g.Key.FullName, g.Key.Method, Amount = g.Sum(x => x.Amount), Count = g.Count() }).ToListAsync(ct);

        var cash = payments.Where(x => x.Method == PaymentMethod.Cash).Sum(x => x.Amount);
        var other = payments.Where(x => x.Method != PaymentMethod.Cash).Sum(x => x.Amount);
        var closed = sessions.Where(s => s.ClosedAt is not null && s.CountedAmount is not null && s.ExpectedAmount is not null).ToList();
        decimal Difference(SessionRow s) => s.CountedAmount!.Value - s.ExpectedAmount!.Value;

        var cashiers = payments.GroupBy(x => x.Cashier).OrderByDescending(g => g.Sum(x => x.Amount))
            .Select(g => Row(g.Key, g.Sum(x => x.Count), g.Where(x => x.Method == PaymentMethod.Cash).Sum(x => x.Amount), g.Where(x => x.Method != PaymentMethod.Cash).Sum(x => x.Amount), g.Sum(x => x.Amount))).ToList();

        return new ReportDocument(
            "صندوق و دریافت‌ها", "هر صندوق‌دار چقدر پول گرفت و آیا پول شمارش‌شده با حساب سیستم یکی بود.", p.Start, p.End,
            [
                MoneyTile("دریافت نقد", cash),
                MoneyTile("دریافت غیرنقد", other),
                CountTile("صندوق‌های هنوز باز", sessions.Count(s => s.ClosedAt is null)),
                MoneyTile("کسری یا اضافه صندوق", closed.Sum(Difference)),
            ],
            null,
            [
                new ReportTable("دریافت هر صندوق‌دار", [Text("صندوق‌دار"), Count("تعداد رسید"), Money("نقد"), Money("غیرنقد"), Money("مجموع")], cashiers,
                    cashiers.Count > 1 ? Row("مجموع", payments.Sum(x => x.Count), cash, other, cash + other) : null),
                new ReportTable("باز و بسته شدن صندوق‌ها",
                    [Text("صندوق"), Text("صندوق‌دار"), new("باز شد", ReportKind.DateTime), new("بسته شد", ReportKind.DateTime), Money("شروع"), Money("دریافت نقد"), Money("باید باشد"), Money("شمارش شد"), Money("کسری یا اضافه")],
                    sessions.Select(s => Row(s.RegisterName, s.OpenedBy, s.OpenedAt, s.ClosedAt, s.OpeningAmount, s.CashCollected, s.ExpectedAmount ?? s.OpeningAmount + s.CashCollected, s.CountedAmount,
                        s.CountedAmount is not null && s.ExpectedAmount is not null ? Difference(s) : null)).ToList(),
                    Note: "منفی یعنی پول کمتر از حساب سیستم بود و مثبت یعنی بیشتر. برگشت وجه و مصارف از مبلغ «باید باشد» کم نمی‌شوند."),
            ]);
    }

    private static async Task<ReportDocument> OutstandingAsync(AppDbContext db, CancellationToken ct)
    {
        var open = db.Invoices.AsNoTracking().Where(i => (i.Status == InvoiceStatus.Issued || i.Status == InvoiceStatus.PartiallyPaid) && i.Total - i.Discount - i.Paid > 0);
        var count = await open.CountAsync(ct);
        var total = await open.SumAsync(i => (decimal?)(i.Total - i.Discount - i.Paid), ct) ?? 0;
        var rows = await open.OrderBy(i => i.Id).Take(MaxListRows)
            .Select(i => new
            {
                i.InvoiceNumber,
                Patient = i.Patient != null ? (i.Patient.FirstName + " " + (i.Patient.LastName ?? "")).Trim() : null,
                File = i.Patient != null ? i.Patient.FileNumber : null,
                Item = i.Items.Where(x => !x.IsVoid).OrderBy(x => x.Id).Select(x => x.Description).FirstOrDefault(),
                i.CreatedAt,
                Payable = i.Total - i.Discount,
                i.Paid,
                Balance = i.Total - i.Discount - i.Paid,
            }).ToListAsync(ct);

        var today = KabulClock.Today;
        int Age(DateTime created) => today.DayNumber - DateOnly.FromDateTime(created.AddHours(4.5)).DayNumber;

        return new ReportDocument(
            "پول‌های گرفته‌نشده", "فاکتورهایی که هنوز کامل پرداخت نشده‌اند، از کهنه‌ترین به تازه‌ترین.", null, null,
            [
                MoneyTile("مجموع طلب", total),
                CountTile("فاکتورهای باز", count),
                CountTile("فاکتورهای کهنه‌تر از ۷ روز", rows.Count(r => Age(r.CreatedAt) > 7)),
            ],
            null,
            [
                new ReportTable("فاکتورهای باز", [Text("فاکتور"), Text("مریض"), Text("شماره پرونده"), Text("بابت"), new("تاریخ", ReportKind.DateTime), Count("چند روز"), Money("قابل پرداخت"), Money("پرداخت‌شده"), Money("مانده")],
                    rows.Select(r => Row(r.InvoiceNumber, r.Patient ?? "بدون مریض", r.File, r.Item, r.CreatedAt, Age(r.CreatedAt), r.Payable, r.Paid, r.Balance)).ToList(),
                    count > 1 && count <= MaxListRows ? Row("مجموع", null, null, null, null, null, rows.Sum(r => r.Payable), rows.Sum(r => r.Paid), rows.Sum(r => r.Balance)) : null,
                    count > MaxListRows ? $"فقط {MaxListRows} فاکتور کهنه‌تر نشان داده شده است." : null),
            ]);
    }

    private static async Task<ReportDocument> AdjustmentsAsync(Period p, AppDbContext db, CancellationToken ct)
    {
        var logs = await (
            from a in db.AuditLogs.AsNoTracking()
            join u in db.Users on a.UserId equals u.Id
            where (a.Action == "InvoiceDiscount" || a.Action == "InvoiceVoided") && a.CreatedAt >= p.StartUtc && a.CreatedAt < p.EndUtc
            orderby a.Id descending
            select new { a.Action, a.EntityId, a.Details, a.CreatedAt, User = u.FullName }).Take(MaxListRows).ToListAsync(ct);

        var invoiceIds = logs.Select(l => long.TryParse(l.EntityId, out var id) ? id : 0).Distinct().ToList();
        var invoices = await db.Invoices.AsNoTracking().Where(i => invoiceIds.Contains(i.Id))
            .Select(i => new { i.Id, i.InvoiceNumber, Patient = i.Patient != null ? (i.Patient.FirstName + " " + (i.Patient.LastName ?? "")).Trim() : null })
            .ToDictionaryAsync(i => i.Id.ToString(CultureInfo.InvariantCulture), ct);

        // The audit line is written as "amount: reason".
        static (decimal Amount, string Reason) Discount(string? details)
        {
            var split = (details ?? "").Split(':', 2);
            return (decimal.TryParse(split[0], NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) ? amount : 0, split.Length > 1 ? split[1].Trim() : "");
        }

        var discounts = logs.Where(l => l.Action == "InvoiceDiscount").Select(l => new { Log = l, Line = Discount(l.Details) }).ToList();
        var voids = logs.Where(l => l.Action == "InvoiceVoided").ToList();

        var refunds = await (
            from r in db.Refunds.AsNoTracking()
            join u in db.Users on r.ApprovedByUserId equals u.Id
            where r.CreatedAt >= p.StartUtc && r.CreatedAt < p.EndUtc
            orderby r.Id descending
            select new
            {
                r.RefundNumber,
                r.Payment!.Invoice!.InvoiceNumber,
                Patient = r.Payment.Invoice.Patient != null ? (r.Payment.Invoice.Patient.FirstName + " " + (r.Payment.Invoice.Patient.LastName ?? "")).Trim() : null,
                r.Amount,
                r.Reason,
                r.CreatedAt,
                User = u.FullName,
            }).Take(MaxListRows).ToListAsync(ct);

        string? InvoiceNumber(string? id) => id is not null && invoices.TryGetValue(id, out var row) ? row.InvoiceNumber : id;
        string? Patient(string? id) => id is not null && invoices.TryGetValue(id, out var row) ? row.Patient : null;

        return new ReportDocument(
            "تخفیف، برگشت وجه و باطل", "برای کنترل: چه کسی، چه وقت و به چه دلیل پول کم یا برگشت داد.", p.Start, p.End,
            [
                MoneyTile("مجموع تخفیف", discounts.Sum(d => d.Line.Amount)),
                MoneyTile("مجموع برگشت وجه", refunds.Sum(r => r.Amount)),
                CountTile("فاکتورهای باطل", voids.Count),
            ],
            null,
            [
                new ReportTable("تخفیف‌ها", [Text("فاکتور"), Text("مریض"), Money("مبلغ"), Text("دلیل"), Text("ثبت‌کننده"), new("زمان", ReportKind.DateTime)],
                    discounts.Select(d => Row(InvoiceNumber(d.Log.EntityId), Patient(d.Log.EntityId), d.Line.Amount, d.Line.Reason, d.Log.User, d.Log.CreatedAt)).ToList()),
                new ReportTable("برگشت وجه", [Text("شماره"), Text("فاکتور"), Text("مریض"), Money("مبلغ"), Text("دلیل"), Text("تأییدکننده"), new("زمان", ReportKind.DateTime)],
                    refunds.Select(r => Row(r.RefundNumber, r.InvoiceNumber, r.Patient, r.Amount, r.Reason, r.User, r.CreatedAt)).ToList()),
                new ReportTable("فاکتورهای باطل", [Text("فاکتور"), Text("مریض"), Text("دلیل"), Text("ثبت‌کننده"), new("زمان", ReportKind.DateTime)],
                    voids.Select(v => Row(InvoiceNumber(v.EntityId), Patient(v.EntityId), v.Details, v.User, v.CreatedAt)).ToList()),
            ]);
    }

    private static async Task<ReportDocument> ExpensesAsync(Period p, AppDbContext db, CancellationToken ct)
    {
        var expenses = db.Expenses.AsNoTracking().Where(e => e.Status == ExpenseStatus.Recorded && e.CreatedAt >= p.StartUtc && e.CreatedAt < p.EndUtc);
        var categories = await expenses.GroupBy(e => e.Category).Select(g => new { Name = g.Key, Count = g.Count(), Amount = g.Sum(e => e.Amount) }).OrderByDescending(r => r.Amount).ToListAsync(ct);
        var total = categories.Sum(r => r.Amount);
        var list = await expenses.OrderByDescending(e => e.Id).Take(MaxListRows).Select(e => new { e.ExpenseNumber, e.Category, e.Description, e.Amount, e.ExpenseDate }).ToListAsync(ct);
        var count = categories.Sum(r => r.Count);

        return new ReportDocument(
            "مصارف", "پول شفاخانه در این دوره در کجا مصرف شد.", p.Start, p.End,
            [
                MoneyTile("مجموع مصارف", total),
                CountTile("تعداد مصارف", count),
                CountTile("نوع مصرف", categories.Count),
            ],
            null,
            [
                new ReportTable("به تفکیک نوع", [Text("نوع مصرف"), Count("تعداد"), Money("مبلغ")], categories.Select(r => Row(r.Name, r.Count, r.Amount)).ToList(),
                    categories.Count > 1 ? Row("مجموع", count, total) : null),
                new ReportTable("فهرست مصارف", [Text("شماره"), new("تاریخ مصرف", ReportKind.Date), Text("نوع"), Text("شرح"), Money("مبلغ")],
                    list.Select(e => Row(e.ExpenseNumber, e.ExpenseDate, e.Category, e.Description, e.Amount)).ToList(),
                    Note: count > MaxListRows ? $"فقط {MaxListRows} مصرف تازه‌تر نشان داده شده است." : null),
            ]);
    }

    private static async Task<ReportDocument> SalesAsync(Period p, AppDbContext db, HttpContext http, CancellationToken ct)
    {
        var seesPharmacy = http.User.HasClaim(RolePermissions.ClaimType, "pharmacy.view");
        var seesOptical = http.User.HasClaim(RolePermissions.ClaimType, "optical.view");
        if (!seesPharmacy && !seesOptical)
        {
            throw new AppException(StatusCodes.Status403Forbidden, "forbidden", "شما به گزارش فروش دسترسی ندارید.");
        }

        var lines = await db.Sales.AsNoTracking()
            .Where(s => s.CreatedAt >= p.StartUtc && s.CreatedAt < p.EndUtc && !s.IsReturned && ((s.Kind == SaleKind.Pharmacy && seesPharmacy) || (s.Kind == SaleKind.Optical && seesOptical)))
            .SelectMany(s => s.Items.Select(i => new { s.Id, s.Kind, i.Product!.Name, i.Quantity, Amount = i.Quantity * i.UnitPrice }))
            .ToListAsync(ct);

        decimal Total(SaleKind kind) => lines.Where(l => l.Kind == kind).Sum(l => l.Amount);
        int Count(SaleKind kind) => lines.Where(l => l.Kind == kind).Select(l => l.Id).Distinct().Count();

        var products = lines.GroupBy(l => new { l.Name, l.Kind })
            .Select(g => new { g.Key.Name, g.Key.Kind, Quantity = g.Sum(l => l.Quantity), Amount = g.Sum(l => l.Amount) })
            .OrderByDescending(r => r.Amount).ThenBy(r => r.Name).ToList();
        var shown = products.Take(50).ToList();
        static string KindLabel(SaleKind kind) => kind == SaleKind.Pharmacy ? "دواخانه" : "عینک";

        var tiles = new List<ReportTile>();
        var tables = new List<ReportTable>();
        if (seesPharmacy)
        {
            tiles.Add(MoneyTile("فروش دواخانه", Total(SaleKind.Pharmacy)));
            tiles.Add(CountTile("تعداد فروش دواخانه", Count(SaleKind.Pharmacy)));
        }

        if (seesOptical)
        {
            tiles.Add(MoneyTile("فروش عینک", Total(SaleKind.Optical)));
            tiles.Add(CountTile("تعداد فروش عینک", Count(SaleKind.Optical)));
        }

        tables.Add(new ReportTable("پرفروش‌ترین اجناس", [Text("جنس"), Text("بخش"), new("تعداد فروخته‌شده", ReportKind.Quantity), new("مبلغ", ReportKind.Money)],
            shown.Select(r => Row(r.Name, KindLabel(r.Kind), r.Quantity, r.Amount)).ToList(),
            Note: products.Count > shown.Count ? $"فقط {shown.Count} جنس پرفروش‌تر نشان داده شده است." : null));

        if (seesPharmacy)
        {
            var prescriptions = await db.MedicalPrescriptions.AsNoTracking().Where(x => x.CreatedAt >= p.StartUtc && x.CreatedAt < p.EndUtc)
                .GroupBy(x => x.Status).Select(g => new { Status = g.Key, Count = g.Count() }).ToListAsync(ct);
            int Rx(PrescriptionStatus status) => prescriptions.FirstOrDefault(x => x.Status == status)?.Count ?? 0;
            tables.Add(CountTable("نسخه‌های دوا", "وضعیت", "تعداد",
            [
                new("کامل تحویل شد", Rx(PrescriptionStatus.Dispensed)),
                new("بخشی تحویل شد", Rx(PrescriptionStatus.PartiallyDispensed)),
                new("هنوز تحویل نشده", Rx(PrescriptionStatus.Issued)),
                new("لغو شده", Rx(PrescriptionStatus.Cancelled)),
            ]));
        }

        if (seesOptical)
        {
            var orders = await db.OpticalOrders.AsNoTracking().Where(o => o.CreatedAt >= p.StartUtc && o.CreatedAt < p.EndUtc)
                .GroupBy(o => o.Status).Select(g => new { Status = g.Key, Count = g.Count() }).ToListAsync(ct);
            int Order(OpticalOrderStatus status) => orders.FirstOrDefault(o => o.Status == status)?.Count ?? 0;
            tables.Add(CountTable("سفارش‌های عینک", "وضعیت", "تعداد",
            [
                new("تحویل شد", Order(OpticalOrderStatus.Delivered)),
                new("آماده تحویل", Order(OpticalOrderStatus.Ready)),
                new("در کارگاه", Order(OpticalOrderStatus.InWorkshop)),
                new("ثبت شده", Order(OpticalOrderStatus.Ordered)),
                new("لغو شده", Order(OpticalOrderStatus.Cancelled)),
            ]));
        }

        return new ReportDocument("فروش دواخانه و عینک", "چقدر فروش شد و کدام اجناس بیشتر رفت. فروش برگشت‌خورده حساب نشده است.", p.Start, p.End, tiles, null, tables);
    }

    private static async Task<ReportDocument> StockAsync(AppDbContext db, CancellationToken ct)
    {
        var today = KabulClock.Today;
        var limit = today.AddDays(NearExpiryDays);

        var batches = db.StockBatches.AsNoTracking().Where(b => b.Quantity > 0);
        var value = await batches.GroupBy(b => b.Product!.Kind)
            .Select(g => new { Kind = g.Key, Value = g.Sum(b => b.Quantity * b.UnitCost), Products = g.Select(b => b.ProductId).Distinct().Count() }).ToListAsync(ct);

        var low = await db.Products.AsNoTracking()
            .Where(x => x.IsActive && x.ReorderLevel > 0)
            .Select(x => new { x.Name, x.Unit, x.ReorderLevel, OnHand = db.StockBatches.Where(b => b.ProductId == x.Id).Sum(b => (decimal?)b.Quantity) ?? 0 })
            .Where(x => x.OnHand <= x.ReorderLevel)
            .OrderBy(x => x.OnHand).ThenBy(x => x.Name)
            .ToListAsync(ct);

        var expiring = await batches.Where(b => b.ExpiryDate != null && b.ExpiryDate <= limit)
            .OrderBy(b => b.ExpiryDate)
            .Select(b => new { Product = b.Product!.Name, b.LotNumber, Location = b.Location!.Name, b.ExpiryDate, b.Quantity, Value = b.Quantity * b.UnitCost })
            .Take(MaxListRows).ToListAsync(ct);

        static string KindLabel(ProductKind kind) => kind switch
        {
            ProductKind.Drug => "دوا",
            ProductKind.Frame => "فریم عینک",
            ProductKind.Lens => "شیشه عینک",
            _ => "عمومی",
        };

        return new ReportDocument(
            "موجودی گدام", "چه چیزی رو به ختم است، تاریخ کدام جنس نزدیک است و ارزش موجودی چقدر است.", null, null,
            [
                MoneyTile("ارزش موجودی", value.Sum(v => v.Value)),
                CountTile("اجناس رو به ختم", low.Count),
                CountTile("بسته‌های تاریخ‌گذشته", expiring.Count(b => b.ExpiryDate < today)),
                CountTile($"تاریخ در {NearExpiryDays} روز آینده", expiring.Count(b => b.ExpiryDate >= today)),
            ],
            null,
            [
                new ReportTable("اجناس رو به ختم", [Text("جنس"), new("موجودی", ReportKind.Quantity), new("حد سفارش", ReportKind.Quantity), Text("واحد")],
                    low.Select(x => Row(x.Name, x.OnHand, x.ReorderLevel, x.Unit)).ToList()),
                new ReportTable("تاریخ‌گذشته و نزدیک به ختم تاریخ", [Text("جنس"), Text("نمبر بسته"), Text("محل"), new("تاریخ ختم", ReportKind.Date), new("موجودی", ReportKind.Quantity), Money("ارزش")],
                    expiring.Select(b => Row(b.Product, b.LotNumber, b.Location, b.ExpiryDate, b.Quantity, b.Value)).ToList(),
                    Note: "این فهرست از تاریخ گذشته‌ترین شروع می‌شود."),
                new ReportTable("ارزش موجودی به تفکیک نوع", [Text("نوع"), Count("تعداد جنس"), Money("ارزش به قیمت خرید")],
                    value.OrderByDescending(v => v.Value).Select(v => Row(KindLabel(v.Kind), v.Products, v.Value)).ToList(),
                    value.Count > 1 ? Row("مجموع", value.Sum(v => v.Products), value.Sum(v => v.Value)) : null),
            ]);
    }
}
