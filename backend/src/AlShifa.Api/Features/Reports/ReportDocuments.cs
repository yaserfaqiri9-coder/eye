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

/// <summary>How a value is shown: count, money, quantity, text, date or datetime.</summary>
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

/// <param name="Note">One plain sentence, only when a reader could otherwise misread the list, such as a list cut short.</param>
public sealed record ReportTable(string Title, IReadOnlyList<ReportColumn> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows, IReadOnlyList<object?>? Total = null, string? Note = null);

public sealed record ReportPoint(DateOnly Date, decimal Value);

public sealed record ReportTrend(string Title, string Kind, IReadOnlyList<ReportPoint> Points);

/// <summary>
/// One report in the single shape the page shows and the Excel and PDF files are made from: at most four figures and one
/// list with the detail behind them. From and To are null on a report of the present moment.
/// </summary>
public sealed record ReportDocument(string Title, string Description, DateOnly? From, DateOnly? To, IReadOnlyList<ReportTile> Tiles, ReportTrend? Trend, IReadOnlyList<ReportTable> Tables);

public static partial class ReportDocuments
{
    /// <summary>A list longer than this is cut, with a sentence saying so; a narrower period shows the rest.</summary>
    private const int MaxListRows = 3000;

    private const int NearExpiryDays = 90;

    private sealed record Period(DateOnly Start, DateOnly End, DateTime StartUtc, DateTime EndUtc);

    private delegate Task<ReportDocument> Builder(Period period, AppDbContext db, HttpContext http, CancellationToken ct);

    /// <param name="Permission">Needed to reach the report at all.</param>
    /// <param name="AlsoAny">When not empty, one of these is needed too: a list that names patients needs the right to see them.</param>
    private sealed record Definition(string Permission, string[] AlsoAny, bool NoPeriod, Builder Build);

    private static readonly Dictionary<string, Definition> Reports = new()
    {
        ["patients"] = new("reports.view", ["patients.view"], false, PatientsAsync),
        ["doctors"] = new("reports.view", [], false, DoctorsAsync),
        ["surgeries"] = new("reports.view", ["surgery.view"], false, SurgeriesAsync),
        ["diagnostics"] = new("reports.view", ["diagnostics.view"], false, DiagnosticsAsync),
        ["diseases"] = new("reports.view", [], false, DiseasesAsync),
        ["cash"] = new("ledger.view", [], false, CashAsync),
        ["outstanding"] = new("ledger.view", [], true, (_, db, _, ct) => OutstandingAsync(db, ct)),
        ["expenses"] = new("ledger.view", [], false, ExpensesAsync),
        ["profit-loss"] = new("ledger.view", [], false, ProfitLossAsync),
        ["revenue-sources"] = new("ledger.view", [], false, RevenueSourcesAsync),
        ["expense-summary"] = new("ledger.view", [], false, ExpenseSummaryAsync),
        ["daily-finance"] = new("ledger.view", [], false, DailyFinanceAsync),
        ["cash-flow"] = new("ledger.view", [], false, CashFlowAsync),
        ["sales"] = new("reports.view", ["pharmacy.view", "optical.view"], false, SalesAsync),
        ["stock"] = new("inventory.view", [], true, (_, db, _, ct) => StockAsync(db, ct)),
    };

    public static void MapReportDocuments(this IEndpointRouteBuilder group)
    {
        foreach (var (key, definition) in Reports)
        {
            group.MapGet($"/{key}", async (DateOnly? from, DateOnly? to, AppDbContext db, HttpContext http, CancellationToken ct) =>
                Results.Ok(await BuildAsync(definition, from, to, db, http, ct))).RequirePermission(definition.Permission);

            // The same document as the page, written as an Excel workbook or a PDF page by page.
            group.MapGet($"/{key}/export", async (string? format, DateOnly? from, DateOnly? to, AppDbContext db, HttpContext http, CancellationToken ct) =>
            {
                var excel = string.Equals(format, "xlsx", StringComparison.OrdinalIgnoreCase);
                if (!excel && !string.Equals(format, "pdf", StringComparison.OrdinalIgnoreCase))
                {
                    throw new AppException(StatusCodes.Status400BadRequest, "invalid_format", "نوع فایل باید xlsx یا pdf باشد.");
                }

                Need(http, excel ? ["reports.export"] : ["reports.print", "reports.export"]);
                var document = await BuildAsync(definition, from, to, db, http, ct);
                var hospital = (await SettingsEndpoints.LoadAsync(db, ct)).HospitalName;
                var name = $"report-{key}-{ReportText.FileDate(document.From ?? KabulClock.Today)}";
                return excel
                    ? Results.File(ReportExcel.Build(document, hospital), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"{name}.xlsx")
                    : Results.File(ReportPdf.Build(document, hospital), "application/pdf", $"{name}.pdf");
            }).RequirePermission(definition.Permission);
        }
    }

    private static Task<ReportDocument> BuildAsync(Definition definition, DateOnly? from, DateOnly? to, AppDbContext db, HttpContext http, CancellationToken ct)
    {
        if (definition.AlsoAny.Length > 0)
        {
            Need(http, definition.AlsoAny);
        }

        var (start, end) = ReportEndpoints.Range(from, to);
        var (startUtc, endUtc) = ReportEndpoints.UtcRange(start, end);
        return definition.Build(new Period(start, end, startUtc, endUtc), db, http, ct);
    }

    private static void Need(HttpContext http, string[] any)
    {
        if (!any.Any(permission => http.User.HasClaim(RolePermissions.ClaimType, permission)))
        {
            throw new AppException(StatusCodes.Status403Forbidden, "forbidden", "شما به این گزارش دسترسی ندارید.");
        }
    }

    private static ReportColumn Text(string label) => new(label, ReportKind.Text);

    private static ReportColumn Count(string label) => new(label, ReportKind.Count);

    private static ReportColumn Money(string label) => new(label, ReportKind.Money);

    private static ReportColumn Quantity(string label) => new(label, ReportKind.Quantity);

    private static ReportColumn Date(string label) => new(label, ReportKind.Date);

    private static ReportColumn Time(string label) => new(label, ReportKind.DateTime);

    private static ReportTile CountTile(string label, decimal value) => new(label, value, ReportKind.Count);

    private static ReportTile MoneyTile(string label, decimal value) => new(label, value, ReportKind.Money);

    private static object?[] Row(params object?[] cells) => cells;

    private static string? Cut(int shown, int total, string what) => total > shown ? $"فقط {shown} {what} اول نشان داده شده است. برای دیدن بقیه، دوره کوتاه‌تری انتخاب کنید." : null;

    private static string FullName(string first, string? last) => $"{first} {last}".Trim();

    private static DateOnly KabulDay(DateTime utc) => DateOnly.FromDateTime(utc.AddHours(4.5));

    private static string GenderName(Gender gender) => gender switch
    {
        Gender.Male => "مرد",
        Gender.Female => "زن",
        _ => "دیگر",
    };

    private static string EyeName(EyeSide eye) => eye switch
    {
        EyeSide.OD => "چشم راست",
        EyeSide.OS => "چشم چپ",
        EyeSide.Both => "هر دو چشم",
        _ => "—",
    };

    private static string VisitStatusName(VisitStatus status) => status switch
    {
        VisitStatus.Registered => "ثبت شده",
        VisitStatus.Waiting => "در انتظار",
        VisitStatus.InPreExam => "معاینه اولیه",
        VisitStatus.ReadyForDoctor => "آماده برای داکتر",
        VisitStatus.InConsultation => "نزد داکتر",
        VisitStatus.AwaitingDiagnostic => "منتظر آزمایش",
        VisitStatus.FollowUpRequired => "نیاز به پیگیری",
        VisitStatus.Completed => "تکمیل شده",
        VisitStatus.Cancelled => "لغو شده",
        _ => "در انتظار پرداخت",
    };

    private static string SurgeryStatusName(SurgeryStatus status) => status switch
    {
        SurgeryStatus.Proposed => "پیشنهاد شده",
        SurgeryStatus.Scheduled => "وقت داده شده",
        SurgeryStatus.InProgress => "در جریان",
        SurgeryStatus.Completed => "انجام شده",
        SurgeryStatus.FollowUp => "انجام شده، در پیگیری",
        SurgeryStatus.Closed => "انجام شده، بسته",
        _ => "لغو شده",
    };

    private static string DiagnosticStatusName(DiagnosticStatus status) => status switch
    {
        DiagnosticStatus.Requested => "درخواست شده",
        DiagnosticStatus.InProgress => "در جریان",
        DiagnosticStatus.ResultReady => "نتیجه آماده",
        _ => "لغو شده",
    };

    private static string DiagnosticKindName(DiagnosticKind kind) => kind switch
    {
        DiagnosticKind.Lab => "آزمایشگاه",
        DiagnosticKind.Refraction => "رفرکشن",
        _ => "عکس‌برداری",
    };

    /// <summary>Every visit of the period, one line each: who came, when, to whom, and how it ended.</summary>
    private static async Task<ReportDocument> PatientsAsync(Period p, AppDbContext db, HttpContext http, CancellationToken ct)
    {
        var visits = db.Visits.AsNoTracking().Where(v => v.VisitDate >= p.Start && v.VisitDate <= p.End && v.Status != VisitStatus.AwaitingPayment);
        var attended = visits.Where(v => v.Status != VisitStatus.Cancelled);
        var total = await visits.CountAsync(ct);

        var rows = await visits.OrderBy(v => v.VisitDate).ThenBy(v => v.QueueNumber).ThenBy(v => v.Id).Take(MaxListRows)
            .Select(v => new
            {
                v.VisitDate,
                v.QueueNumber,
                v.Patient!.FileNumber,
                v.Patient.FirstName,
                v.Patient.LastName,
                v.Patient.Gender,
                v.Patient.BirthDate,
                v.Patient.EstimatedBirthYear,
                Department = v.Department!.Name,
                Doctor = v.Doctor != null ? v.Doctor.FullName : null,
                v.Status,
            }).ToListAsync(ct);

        return new ReportDocument(
            "مراجعات مریضان", "هر مراجعه در یک سطر: تاریخ، مریض، بخش، داکتر و وضعیت.", p.Start, p.End,
            [
                CountTile("مراجعات", await attended.CountAsync(ct)),
                CountTile("مریضان", await attended.Select(v => v.PatientId).Distinct().CountAsync(ct)),
                CountTile("مریضان جدید", await db.Patients.AsNoTracking().CountAsync(x => x.CreatedAt >= p.StartUtc && x.CreatedAt < p.EndUtc, ct)),
                CountTile("لغو شده", await visits.CountAsync(v => v.Status == VisitStatus.Cancelled, ct)),
            ],
            null,
            [
                new ReportTable("فهرست مراجعات",
                    [Date("تاریخ"), Text("نوبت"), Text("شماره پرونده"), Text("نام مریض"), Text("جنسیت"), Count("سن"), Text("بخش"), Text("داکتر"), Text("وضعیت")],
                    rows.Select(r => Row(r.VisitDate, r.QueueNumber?.ToString(), r.FileNumber, FullName(r.FirstName, r.LastName), GenderName(r.Gender),
                        PatientEndpoints.AgeOf(r.BirthDate, r.EstimatedBirthYear), r.Department, r.Doctor ?? "—", VisitStatusName(r.Status))).ToList(),
                    Note: Cut(rows.Count, total, "مراجعه")),
            ]);
    }

    /// <summary>One line per doctor with what they did in the period. Counts only.</summary>
    private static async Task<ReportDocument> DoctorsAsync(Period p, AppDbContext db, HttpContext http, CancellationToken ct)
    {
        var visits = await db.Visits.AsNoTracking()
            .Where(v => v.VisitDate >= p.Start && v.VisitDate <= p.End && v.Status != VisitStatus.AwaitingPayment && v.Status != VisitStatus.Cancelled && v.Doctor != null)
            .GroupBy(v => v.Doctor!.FullName).Select(g => new { Name = g.Key, Count = g.Count() }).ToListAsync(ct);
        var finalized = await (
            from c in db.Consultations.AsNoTracking()
            join u in db.Users on c.AuthorUserId equals u.Id
            where c.FinalizedAt != null && c.FinalizedAt >= p.StartUtc && c.FinalizedAt < p.EndUtc
            group c by u.FullName into g
            select new { Name = g.Key, Count = g.Count() }).ToListAsync(ct);
        var prescriptions = await (
            from x in db.MedicalPrescriptions.AsNoTracking()
            join u in db.Users on x.DoctorUserId equals u.Id
            where x.CreatedAt >= p.StartUtc && x.CreatedAt < p.EndUtc && x.Status != PrescriptionStatus.Cancelled
            group x by u.FullName into g
            select new { Name = g.Key, Count = g.Count() }).ToListAsync(ct);
        var surgeries = await (
            from x in db.SurgeryCases.AsNoTracking()
            join u in db.Users on x.ResponsibleUserId equals u.Id
            where x.CreatedAt >= p.StartUtc && x.CreatedAt < p.EndUtc && x.Status != SurgeryStatus.Cancelled
            group x by u.FullName into g
            select new { Name = g.Key, Count = g.Count() }).ToListAsync(ct);

        int Visits(string name) => visits.FirstOrDefault(r => r.Name == name)?.Count ?? 0;
        int Finalized(string name) => finalized.FirstOrDefault(r => r.Name == name)?.Count ?? 0;
        int Prescriptions(string name) => prescriptions.FirstOrDefault(r => r.Name == name)?.Count ?? 0;
        int Surgeries(string name) => surgeries.FirstOrDefault(r => r.Name == name)?.Count ?? 0;

        var names = visits.Select(r => r.Name).Concat(finalized.Select(r => r.Name)).Concat(prescriptions.Select(r => r.Name)).Concat(surgeries.Select(r => r.Name))
            .Distinct().OrderByDescending(Visits).ThenBy(name => name).ToList();

        return new ReportDocument(
            "کار داکتران", "هر داکتر در این دوره چند مریض دید، چند معاینه را نهایی کرد و چند نسخه و عملیات ثبت کرد.", p.Start, p.End,
            [
                CountTile("داکتران", names.Count),
                CountTile("مراجعات", visits.Sum(r => r.Count)),
                CountTile("معاینات نهایی‌شده", finalized.Sum(r => r.Count)),
                CountTile("عملیات", surgeries.Sum(r => r.Count)),
            ],
            null,
            [
                new ReportTable("کار هر داکتر", [Text("داکتر"), Count("مراجعات"), Count("معاینات نهایی‌شده"), Count("نسخه دوا"), Count("عملیات")],
                    names.Select(name => Row(name, Visits(name), Finalized(name), Prescriptions(name), Surgeries(name))).ToList(),
                    names.Count > 1 ? Row("مجموع", visits.Sum(r => r.Count), finalized.Sum(r => r.Count), prescriptions.Sum(r => r.Count), surgeries.Sum(r => r.Count)) : null),
            ]);
    }

    /// <summary>Every surgery case opened in the period, one line each.</summary>
    private static async Task<ReportDocument> SurgeriesAsync(Period p, AppDbContext db, HttpContext http, CancellationToken ct)
    {
        var cases = db.SurgeryCases.AsNoTracking().Where(s => s.CreatedAt >= p.StartUtc && s.CreatedAt < p.EndUtc);
        var statuses = await cases.GroupBy(s => s.Status).Select(g => new { Status = g.Key, Count = g.Count() }).ToListAsync(ct);
        int Of(params SurgeryStatus[] wanted) => statuses.Where(s => wanted.Contains(s.Status)).Sum(s => s.Count);

        var rows = await (
            from s in cases
            join u in db.Users on s.ResponsibleUserId equals u.Id
            orderby s.CreatedAt
            select new { s.CreatedAt, s.CaseNumber, s.Patient!.FileNumber, s.Patient.FirstName, s.Patient.LastName, s.SurgeryType, s.Eye, Doctor = u.FullName, s.ScheduledAt, s.Status })
            .Take(MaxListRows).ToListAsync(ct);

        return new ReportDocument(
            "عملیات‌ها", "هر عملیات ثبت‌شده در این دوره در یک سطر: مریض، نوع عملیات، چشم، داکتر و وضعیت.", p.Start, p.End,
            [
                CountTile("همه عملیات‌ها", statuses.Sum(s => s.Count) - Of(SurgeryStatus.Cancelled)),
                CountTile("انجام شده", Of(SurgeryStatus.Completed, SurgeryStatus.FollowUp, SurgeryStatus.Closed)),
                CountTile("در انتظار", Of(SurgeryStatus.Proposed, SurgeryStatus.Scheduled, SurgeryStatus.InProgress)),
                CountTile("لغو شده", Of(SurgeryStatus.Cancelled)),
            ],
            null,
            [
                new ReportTable("فهرست عملیات‌ها",
                    [Date("تاریخ ثبت"), Text("شماره"), Text("شماره پرونده"), Text("نام مریض"), Text("نوع عملیات"), Text("چشم"), Text("داکتر"), Time("وقت عملیات"), Text("وضعیت")],
                    rows.Select(r => Row(KabulDay(r.CreatedAt), r.CaseNumber, r.FileNumber, FullName(r.FirstName, r.LastName), r.SurgeryType, EyeName(r.Eye), r.Doctor, r.ScheduledAt, SurgeryStatusName(r.Status))).ToList(),
                    Note: Cut(rows.Count, statuses.Sum(s => s.Count), "عملیات")),
            ]);
    }

    /// <summary>Every lab, refraction and imaging request of the period, one line each.</summary>
    private static async Task<ReportDocument> DiagnosticsAsync(Period p, AppDbContext db, HttpContext http, CancellationToken ct)
    {
        var orders = db.DiagnosticOrders.AsNoTracking().Where(o => o.CreatedAt >= p.StartUtc && o.CreatedAt < p.EndUtc);
        var statuses = await orders.GroupBy(o => o.Status).Select(g => new { Status = g.Key, Count = g.Count() }).ToListAsync(ct);
        int Of(params DiagnosticStatus[] wanted) => statuses.Where(s => wanted.Contains(s.Status)).Sum(s => s.Count);

        var rows = await orders.OrderBy(o => o.CreatedAt).Take(MaxListRows)
            .Select(o => new { o.CreatedAt, o.OrderNumber, o.Patient!.FileNumber, o.Patient.FirstName, o.Patient.LastName, Test = o.Test!.Name, o.Test.Kind, o.Status })
            .ToListAsync(ct);

        return new ReportDocument(
            "آزمایش و عکس‌برداری", "هر درخواست آزمایش، رفرکشن و عکس‌برداری در یک سطر با وضعیت آن.", p.Start, p.End,
            [
                CountTile("همه درخواست‌ها", statuses.Sum(s => s.Count) - Of(DiagnosticStatus.Cancelled)),
                CountTile("نتیجه آماده", Of(DiagnosticStatus.ResultReady)),
                CountTile("هنوز باز", Of(DiagnosticStatus.Requested, DiagnosticStatus.InProgress)),
                CountTile("لغو شده", Of(DiagnosticStatus.Cancelled)),
            ],
            null,
            [
                new ReportTable("فهرست درخواست‌ها",
                    [Time("زمان"), Text("شماره"), Text("شماره پرونده"), Text("نام مریض"), Text("خدمت"), Text("بخش"), Text("وضعیت")],
                    rows.Select(r => Row(r.CreatedAt, r.OrderNumber, r.FileNumber, FullName(r.FirstName, r.LastName), r.Test, DiagnosticKindName(r.Kind), DiagnosticStatusName(r.Status))).ToList(),
                    Note: Cut(rows.Count, statuses.Sum(s => s.Count), "درخواست")),
            ]);
    }

    /// <summary>Which diagnoses were made most, counted from finalized examinations only. No patient is named.</summary>
    private static async Task<ReportDocument> DiseasesAsync(Period p, AppDbContext db, HttpContext http, CancellationToken ct)
    {
        var finalized = db.Diagnoses.AsNoTracking().Where(d => d.Consultation!.FinalizedAt != null && d.Consultation.FinalizedAt >= p.StartUtc && d.Consultation.FinalizedAt < p.EndUtc);
        var rows = await finalized.GroupBy(d => d.Text)
            .Select(g => new { Name = g.Key, Count = g.Count(), Right = g.Count(d => d.Eye == EyeSide.OD), Left = g.Count(d => d.Eye == EyeSide.OS), Both = g.Count(d => d.Eye == EyeSide.Both) })
            .OrderByDescending(r => r.Count).ThenBy(r => r.Name).ToListAsync(ct);

        return new ReportDocument(
            "بیماری‌های شایع", "کدام تشخیص‌ها در این دوره بیشتر ثبت شده‌اند، از بیشتر به کمتر.", p.Start, p.End,
            [
                CountTile("تشخیص‌های ثبت‌شده", rows.Sum(r => r.Count)),
                CountTile("نوع بیماری", rows.Count),
                CountTile("مریضان", await finalized.Select(d => d.Consultation!.Visit!.PatientId).Distinct().CountAsync(ct)),
            ],
            null,
            [
                new ReportTable("تشخیص‌ها از بیشتر به کمتر", [Text("تشخیص"), Count("تعداد"), Count("چشم راست"), Count("چشم چپ"), Count("هر دو چشم")],
                    rows.Select(r => Row(r.Name, r.Count, r.Right, r.Left, r.Both)).ToList(),
                    rows.Count > 1 ? Row("مجموع", rows.Sum(r => r.Count), rows.Sum(r => r.Right), rows.Sum(r => r.Left), rows.Sum(r => r.Both)) : null),
            ]);
    }

    /// <summary>Every receipt of the period, one line each, with who took it and how.</summary>
    private static async Task<ReportDocument> CashAsync(Period p, AppDbContext db, HttpContext http, CancellationToken ct)
    {
        var payments = db.Payments.AsNoTracking().Where(x => x.CreatedAt >= p.StartUtc && x.CreatedAt < p.EndUtc);
        var totals = await payments.GroupBy(x => x.Method).Select(g => new { Method = g.Key, Amount = g.Sum(x => x.Amount), Count = g.Count() }).ToListAsync(ct);
        var cash = totals.Where(t => t.Method == PaymentMethod.Cash).Sum(t => t.Amount);
        var other = totals.Where(t => t.Method != PaymentMethod.Cash).Sum(t => t.Amount);
        var count = totals.Sum(t => t.Count);

        var rows = await (
            from x in payments
            join u in db.Users on x.ReceivedByUserId equals u.Id
            orderby x.CreatedAt
            select new
            {
                x.CreatedAt,
                x.ReceiptNumber,
                x.Invoice!.InvoiceNumber,
                File = x.Invoice.Patient != null ? x.Invoice.Patient.FileNumber : null,
                First = x.Invoice.Patient != null ? x.Invoice.Patient.FirstName : null,
                Last = x.Invoice.Patient != null ? x.Invoice.Patient.LastName : null,
                x.Method,
                Cashier = u.FullName,
                x.Amount,
            }).Take(MaxListRows).ToListAsync(ct);

        return new ReportDocument(
            "دریافت‌ها", "هر پولی که در این دوره در صندوق گرفته شد، در یک سطر.", p.Start, p.End,
            [
                MoneyTile("مجموع دریافت", cash + other),
                MoneyTile("نقد", cash),
                MoneyTile("غیرنقد", other),
                CountTile("تعداد رسید", count),
            ],
            null,
            [
                new ReportTable("فهرست دریافت‌ها",
                    [Time("زمان"), Text("رسید"), Text("فاکتور"), Text("شماره پرونده"), Text("نام مریض"), Text("روش"), Text("صندوق‌دار"), Money("مبلغ")],
                    rows.Select(r => Row(r.CreatedAt, r.ReceiptNumber, r.InvoiceNumber, r.File ?? "—", r.First is null ? "بدون مریض" : FullName(r.First, r.Last),
                        r.Method == PaymentMethod.Cash ? "نقد" : "غیرنقد", r.Cashier, r.Amount)).ToList(),
                    rows.Count > 1 && rows.Count == count ? Row("مجموع", null, null, null, null, null, null, rows.Sum(r => r.Amount)) : null,
                    Cut(rows.Count, count, "رسید")),
            ]);
    }

    /// <summary>Invoices not yet fully paid, oldest first.</summary>
    private static async Task<ReportDocument> OutstandingAsync(AppDbContext db, CancellationToken ct)
    {
        var open = db.Invoices.AsNoTracking().Where(i => (i.Status == InvoiceStatus.Issued || i.Status == InvoiceStatus.PartiallyPaid) && i.Total - i.Discount - i.Paid > 0);
        var count = await open.CountAsync(ct);
        var total = await open.SumAsync(i => (decimal?)(i.Total - i.Discount - i.Paid), ct) ?? 0;
        var rows = await open.OrderBy(i => i.Id).Take(MaxListRows)
            .Select(i => new
            {
                i.InvoiceNumber,
                i.CreatedAt,
                File = i.Patient != null ? i.Patient.FileNumber : null,
                First = i.Patient != null ? i.Patient.FirstName : null,
                Last = i.Patient != null ? i.Patient.LastName : null,
                Item = i.Items.Where(x => !x.IsVoid).OrderBy(x => x.Id).Select(x => x.Description).FirstOrDefault(),
                Payable = i.Total - i.Discount,
                i.Paid,
                Balance = i.Total - i.Discount - i.Paid,
            }).ToListAsync(ct);

        var today = KabulClock.Today;
        int Age(DateTime created) => today.DayNumber - KabulDay(created).DayNumber;

        return new ReportDocument(
            "طلبات", "فاکتورهایی که هنوز کامل پرداخت نشده‌اند، از کهنه‌ترین به تازه‌ترین.", null, null,
            [
                MoneyTile("مجموع طلب", total),
                CountTile("فاکتورهای باز", count),
                CountTile("کهنه‌تر از ۷ روز", rows.Count(r => Age(r.CreatedAt) > 7)),
            ],
            null,
            [
                new ReportTable("فاکتورهای پرداخت‌نشده",
                    [Date("تاریخ"), Text("فاکتور"), Text("شماره پرونده"), Text("نام مریض"), Text("بابت"), Count("چند روز"), Money("مبلغ فاکتور"), Money("پرداخت‌شده"), Money("باقی")],
                    rows.Select(r => Row(KabulDay(r.CreatedAt), r.InvoiceNumber, r.File ?? "—", r.First is null ? "بدون مریض" : FullName(r.First, r.Last), r.Item ?? "—",
                        Age(r.CreatedAt), r.Payable, r.Paid, r.Balance)).ToList(),
                    count > 1 && count <= MaxListRows ? Row("مجموع", null, null, null, null, null, rows.Sum(r => r.Payable), rows.Sum(r => r.Paid), rows.Sum(r => r.Balance)) : null,
                    Cut(rows.Count, count, "فاکتور")),
            ]);
    }

    /// <summary>Every recorded expense of the period, one line each.</summary>
    private static async Task<ReportDocument> ExpensesAsync(Period p, AppDbContext db, HttpContext http, CancellationToken ct)
    {
        var expenses = db.Expenses.AsNoTracking().Where(e => e.Status == ExpenseStatus.Recorded && e.CreatedAt >= p.StartUtc && e.CreatedAt < p.EndUtc);
        var count = await expenses.CountAsync(ct);
        var total = await expenses.SumAsync(e => (decimal?)e.Amount, ct) ?? 0;
        var rows = await expenses.OrderBy(e => e.ExpenseDate).ThenBy(e => e.Id).Take(MaxListRows)
            .Select(e => new { e.ExpenseDate, e.ExpenseNumber, e.Category, e.Description, e.Amount }).ToListAsync(ct);

        return new ReportDocument(
            "مصارف", "هر مصرف ثبت‌شده در این دوره در یک سطر.", p.Start, p.End,
            [
                MoneyTile("مجموع مصارف", total),
                CountTile("تعداد مصارف", count),
            ],
            null,
            [
                new ReportTable("فهرست مصارف", [Date("تاریخ"), Text("شماره"), Text("نوع مصرف"), Text("شرح"), Money("مبلغ")],
                    rows.Select(e => Row(e.ExpenseDate, e.ExpenseNumber, e.Category, e.Description ?? "—", e.Amount)).ToList(),
                    count > 1 && count <= MaxListRows ? Row("مجموع", null, null, null, total) : null,
                    Cut(rows.Count, count, "مصرف")),
            ]);
    }

    /// <summary>Every delivered sale of the period, one line each. Each department sees only its own sales.</summary>
    private static async Task<ReportDocument> SalesAsync(Period p, AppDbContext db, HttpContext http, CancellationToken ct)
    {
        var seesPharmacy = http.User.HasClaim(RolePermissions.ClaimType, "pharmacy.view");
        var seesOptical = http.User.HasClaim(RolePermissions.ClaimType, "optical.view");

        // A bill counts as a sale once its goods were handed over; a returned sale is left out.
        var sales = db.Sales.AsNoTracking()
            .Where(s => s.DeliveredAt >= p.StartUtc && s.DeliveredAt < p.EndUtc && !s.IsReturned && ((s.Kind == SaleKind.Pharmacy && seesPharmacy) || (s.Kind == SaleKind.Optical && seesOptical)));
        var lines = await sales.SelectMany(s => s.Items.Select(i => new { s.Id, s.Kind, Amount = i.Quantity * i.UnitPrice })).ToListAsync(ct);
        var totals = lines.GroupBy(l => l.Kind).Select(g => new { Kind = g.Key, Count = g.Select(l => l.Id).Distinct().Count(), Amount = g.Sum(l => l.Amount) }).ToList();
        var count = totals.Sum(t => t.Count);

        var rows = await sales.OrderBy(s => s.DeliveredAt).Take(MaxListRows)
            .Select(s => new
            {
                s.DeliveredAt,
                s.SaleNumber,
                s.Kind,
                File = s.Patient != null ? s.Patient.FileNumber : null,
                First = s.Patient != null ? s.Patient.FirstName : null,
                Last = s.Patient != null ? s.Patient.LastName : null,
                Items = s.Items.Select(i => new { i.Product!.Name, i.Quantity }).ToList(),
                Amount = s.Items.Sum(i => i.Quantity * i.UnitPrice),
            }).ToListAsync(ct);

        static string Goods(IEnumerable<(string Name, decimal Quantity)> items) =>
            string.Join("، ", items.Select(i => $"{i.Name} ({ReportText.Number(i.Quantity)})"));

        var tiles = new List<ReportTile>();
        if (seesPharmacy)
        {
            tiles.Add(MoneyTile("فروش دواخانه", totals.Where(t => t.Kind == SaleKind.Pharmacy).Sum(t => t.Amount)));
            tiles.Add(CountTile("تعداد فروش دواخانه", totals.Where(t => t.Kind == SaleKind.Pharmacy).Sum(t => t.Count)));
        }

        if (seesOptical)
        {
            tiles.Add(MoneyTile("فروش عینک", totals.Where(t => t.Kind == SaleKind.Optical).Sum(t => t.Amount)));
            tiles.Add(CountTile("تعداد فروش عینک", totals.Where(t => t.Kind == SaleKind.Optical).Sum(t => t.Count)));
        }

        return new ReportDocument(
            "فروش دواخانه و عینک", "هر فروش تحویل‌شده در این دوره در یک سطر با اجناس و مبلغ آن.", p.Start, p.End, tiles, null,
            [
                new ReportTable("فهرست فروش‌ها", [Time("زمان تحویل"), Text("شماره"), Text("بخش"), Text("نام مریض"), Text("اجناس"), Money("مبلغ")],
                    rows.Select(r => Row(r.DeliveredAt, r.SaleNumber, r.Kind == SaleKind.Pharmacy ? "دواخانه" : "عینک",
                        r.First is null ? "فروش مستقل" : FullName(r.First, r.Last), Goods(r.Items.Select(i => (i.Name, i.Quantity))), r.Amount)).ToList(),
                    rows.Count > 1 && rows.Count == count ? Row("مجموع", null, null, null, null, rows.Sum(r => r.Amount)) : null,
                    Cut(rows.Count, count, "فروش")),
            ]);
    }

    /// <summary>Every active product with what is left, its nearest expiry, its value and one plain word on its state, problems first.</summary>
    private static async Task<ReportDocument> StockAsync(AppDbContext db, CancellationToken ct)
    {
        var today = KabulClock.Today;
        var soon = today.AddDays(NearExpiryDays);

        var products = await db.Products.AsNoTracking().Where(x => x.IsActive)
            .Select(x => new
            {
                x.Name,
                x.Code,
                x.Kind,
                x.Unit,
                x.ReorderLevel,
                OnHand = db.StockBatches.Where(b => b.ProductId == x.Id).Sum(b => (decimal?)b.Quantity) ?? 0,
                Value = db.StockBatches.Where(b => b.ProductId == x.Id && b.Quantity > 0).Sum(b => (decimal?)(b.Quantity * b.UnitCost)) ?? 0,
                Expiry = db.StockBatches.Where(b => b.ProductId == x.Id && b.Quantity > 0 && b.ExpiryDate != null).Min(b => b.ExpiryDate),
            }).ToListAsync(ct);

        // The order is the urgency: what is gone or spoiled first, ordinary stock last.
        (int Rank, string Name) State(decimal onHand, decimal reorder, DateOnly? expiry) =>
            onHand <= 0 ? (0, "ختم شده")
            : expiry < today ? (1, "تاریخ گذشته")
            : reorder > 0 && onHand <= reorder ? (2, "رو به ختم")
            : expiry <= soon ? (3, "نزدیک به ختم تاریخ")
            : (4, "عادی");

        static string KindName(ProductKind kind) => kind switch
        {
            ProductKind.Drug => "دوا",
            ProductKind.Frame => "فریم عینک",
            ProductKind.Lens => "شیشه عینک",
            _ => "عمومی",
        };

        var rows = products.Select(x => new { x, State = State(x.OnHand, x.ReorderLevel, x.Expiry) })
            .OrderBy(r => r.State.Rank).ThenBy(r => r.x.Name).ToList();
        var shown = rows.Take(MaxListRows).ToList();

        return new ReportDocument(
            "موجودی گدام", "هر جنس با مقدار باقی‌مانده، نزدیک‌ترین تاریخ ختم و ارزش آن. اجناسی که توجه می‌خواهند اول آمده‌اند.", null, null,
            [
                MoneyTile("ارزش موجودی", products.Sum(x => x.Value)),
                CountTile("اجناس", products.Count),
                CountTile("ختم یا رو به ختم", rows.Count(r => r.State.Rank is 0 or 2)),
                CountTile("تاریخ گذشته یا نزدیک", rows.Count(r => r.State.Rank is 1 or 3)),
            ],
            null,
            [
                new ReportTable("فهرست اجناس",
                    [Text("جنس"), Text("کد"), Text("نوع"), Quantity("موجودی"), Text("واحد"), Quantity("حد سفارش"), Date("نزدیک‌ترین تاریخ ختم"), Money("ارزش"), Text("وضعیت")],
                    shown.Select(r => Row(r.x.Name, r.x.Code, KindName(r.x.Kind), r.x.OnHand, r.x.Unit, r.x.ReorderLevel > 0 ? r.x.ReorderLevel : null, r.x.Expiry, r.x.Value, r.State.Name)).ToList(),
                    rows.Count > 1 && shown.Count == rows.Count ? Row("مجموع", null, null, null, null, null, null, products.Sum(x => x.Value), null) : null,
                    Cut(shown.Count, rows.Count, "جنس")),
            ]);
    }
}
