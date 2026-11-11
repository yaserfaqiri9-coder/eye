using System.Globalization;
using System.IO.Compression;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using AlShifa.Api.Features.Finance;
using AlShifa.Api.Features.Sync;
using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;

namespace AlShifa.Api.Features.Inventory;

/// <summary>
/// Operator command for a typed opening stock sheet (.xlsx or .csv), one row per batch:
///   AlShifa.Api --import-opening-stock stock.xlsx [--report report.csv]                                         (dry run)
///   AlShifa.Api --import-opening-stock stock.xlsx --apply --backup-taken --expect-import N [--report report.csv]
/// A row becomes stock only when nothing on it is unclear. A row is recorded once, keyed on item, unit, location, batch and expiry:
/// a second run adds nothing, and the same key with another quantity stops the row.
/// A known purchase price posts the stock's value to Inventory against Opening balance; an unknown one records the stock at no cost
/// and posts nothing. A new item is for sale only when its sale price is on the sheet.
/// </summary>
public static class OpeningStockSheet
{
    public const string ExpectFlag = "--expect-import";
    public const string ReportFlag = "--report";
    public const string LedgerKind = "OpeningStock";
    public const string NoDose = "-";

    public static bool Handles(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".xlsx" or ".csv";

    // ── Reading ──────────────────────────────────────────────────────────

    public sealed record SheetRow(
        int Line, string Name, string Dose, string Unit, string Quantity, string PurchasePrice, string SalePrice,
        string Batch, string Expiry, string Location, string Kind, string Form);

    private static readonly Dictionary<string, string[]> Headers = new()
    {
        ["name"] = ["نام", "نام جنس", "name"],
        ["dose"] = ["دوز", "دوز/غلظت", "غلظت", "dose", "strength"],
        ["unit"] = ["واحد", "unit"],
        ["quantity"] = ["تعداد", "quantity", "qty"],
        ["purchase"] = ["قیمت خرید", "purchase price", "unit cost"],
        ["sale"] = ["قیمت فروش", "sale price"],
        ["batch"] = ["شماره بچ", "بچ", "batch", "lot"],
        ["expiry"] = ["تاریخ انقضا", "انقضا", "expiry"],
        ["location"] = ["محل", "محل نگهداری", "location"],
        ["kind"] = ["نوع", "نوع جنس", "kind"],
        ["form"] = ["شکل", "شکل دوا", "form", "category"],
    };

    private static readonly string[] Required = ["name", "dose", "unit", "quantity", "purchase", "sale", "batch", "expiry", "location", "kind"];

    public static List<SheetRow> Read(string path)
    {
        var table = Path.GetExtension(path).ToLowerInvariant() == ".csv" ? ReadCsv(path) : ReadXlsx(path);
        var headerIndex = table.FindIndex(r => r.Any(c => !string.IsNullOrWhiteSpace(c)));
        if (headerIndex < 0)
        {
            throw new InvalidOperationException("The sheet is empty. Nothing was changed.");
        }

        var header = table[headerIndex].Select(Normalize).ToList();
        var columns = new Dictionary<string, int>();
        foreach (var (field, names) in Headers)
        {
            var index = header.FindIndex(h => names.Any(n => Normalize(n) == h));
            if (index >= 0)
            {
                columns[field] = index;
            }
        }

        var missing = Required.Where(f => !columns.ContainsKey(f)).Select(f => Headers[f][0]).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException($"Missing columns: {string.Join(", ", missing)}. Use the template. Nothing was changed.");
        }

        string Cell(List<string> r, string field) =>
            columns.TryGetValue(field, out var i) && i < r.Count ? Clean(r[i]) : "";

        var rows = new List<SheetRow>();
        for (var i = headerIndex + 1; i < table.Count; i++)
        {
            var r = table[i];
            if (r.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            rows.Add(new(i + 1, Cell(r, "name"), Cell(r, "dose"), Cell(r, "unit"), Cell(r, "quantity"), Cell(r, "purchase"), Cell(r, "sale"),
                Cell(r, "batch"), Cell(r, "expiry"), Cell(r, "location"), Cell(r, "kind"), Cell(r, "form")));
        }

        return rows;
    }

    private static List<List<string>> ReadCsv(string path)
    {
        var text = File.ReadAllText(path, Encoding.UTF8);
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') { quoted = false; }
                else { cell.Append(c); }
            }
            else if (c == '"') { quoted = true; }
            else if (c == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\n') { row.Add(cell.ToString()); cell.Clear(); rows.Add(row); row = []; }
            else if (c != '\r') { cell.Append(c); }
        }

        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            rows.Add(row);
        }

        return rows;
    }

    /// <summary>The first worksheet. A date cell arrives as yyyy-MM-dd; every other cell as its text.</summary>
    private static List<List<string>> ReadXlsx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        XNamespace main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XNamespace rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        XNamespace pkg = "http://schemas.openxmlformats.org/package/2006/relationships";
        XDocument Load(string name) => XDocument.Load((zip.GetEntry(name) ?? throw new InvalidOperationException($"Not an Excel file ({name} missing). Nothing was changed.")).Open());

        var shared = zip.GetEntry("xl/sharedStrings.xml") is null ? [] :
            Load("xl/sharedStrings.xml").Root!.Elements(main + "si").Select(si => string.Concat(si.Descendants(main + "t").Select(t => t.Value))).ToList();

        // Built-in date formats and custom formats that contain day/month/year letters.
        var dateStyles = new HashSet<int>();
        if (zip.GetEntry("xl/styles.xml") is not null)
        {
            var styles = Load("xl/styles.xml").Root!;
            var custom = styles.Element(main + "numFmts")?.Elements(main + "numFmt")
                .Where(f => System.Text.RegularExpressions.Regex.IsMatch(((string?)f.Attribute("formatCode") ?? "").Replace("\"", ""), "[dmy]", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                .Select(f => (int)f.Attribute("numFmtId")!).ToHashSet() ?? [];
            var xfs = styles.Element(main + "cellXfs")?.Elements(main + "xf").ToList() ?? [];
            for (var i = 0; i < xfs.Count; i++)
            {
                var id = (int?)xfs[i].Attribute("numFmtId") ?? 0;
                if (id is >= 14 and <= 22 || custom.Contains(id))
                {
                    dateStyles.Add(i);
                }
            }
        }

        var workbook = Load("xl/workbook.xml");
        var firstSheet = workbook.Root!.Element(main + "sheets")!.Elements(main + "sheet").First();
        var relId = (string)firstSheet.Attribute(rel + "id")!;
        var target = (string)Load("xl/_rels/workbook.xml.rels").Root!.Elements(pkg + "Relationship").Single(r => (string?)r.Attribute("Id") == relId).Attribute("Target")!;
        var sheetPath = target.StartsWith('/') ? target.TrimStart('/') : $"xl/{target}";

        var rows = new List<List<string>>();
        foreach (var r in Load(sheetPath).Root!.Element(main + "sheetData")!.Elements(main + "row"))
        {
            var rowNumber = (int)r.Attribute("r")!;
            while (rows.Count < rowNumber - 1)
            {
                rows.Add([]);
            }

            var cells = new List<string>();
            foreach (var c in r.Elements(main + "c"))
            {
                var column = ColumnIndex((string)c.Attribute("r")!);
                while (cells.Count < column)
                {
                    cells.Add("");
                }

                var type = (string?)c.Attribute("t");
                var raw = (string?)c.Element(main + "v");
                var style = (int?)c.Attribute("s") ?? 0;
                cells.Add(type switch
                {
                    "s" => shared[int.Parse(raw!, CultureInfo.InvariantCulture)],
                    "inlineStr" => string.Concat(c.Descendants(main + "t").Select(t => t.Value)),
                    _ when raw is not null && type is null or "n" && dateStyles.Contains(style)
                        && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial)
                        => DateOnly.FromDateTime(DateTime.FromOADate(serial)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    _ => raw ?? "",
                });
            }

            rows.Add(cells);
        }

        return rows;
    }

    private static int ColumnIndex(string reference)
    {
        var index = 0;
        foreach (var c in reference.TakeWhile(char.IsLetter))
        {
            index = index * 26 + (char.ToUpperInvariant(c) - 'A' + 1);
        }

        return index - 1;
    }

    // ── Checking one row ─────────────────────────────────────────────────

    public enum Outcome { Import, AlreadyImported, Held, Error }

    public sealed record Parsed(string ProductName, ProductKind Kind, string Unit, decimal Quantity, decimal? UnitCost, decimal? SalePrice,
        string Batch, DateOnly? Expiry, string LocationCode, string? Form);

    public sealed record Decision(SheetRow Row, Outcome Outcome, string Reason, Parsed? Item = null, string? Key = null);

    public sealed record Location(string Code, string Name);

    private static readonly Dictionary<string, ProductKind> Kinds = new()
    {
        ["دوا"] = ProductKind.Drug, ["drug"] = ProductKind.Drug,
        ["فریم"] = ProductKind.Frame, ["frame"] = ProductKind.Frame,
        ["عدسیه"] = ProductKind.Lens, ["lens"] = ProductKind.Lens,
        ["عمومی"] = ProductKind.General, ["مواد"] = ProductKind.General, ["general"] = ProductKind.General,
    };

    /// <summary>Decides one row from the sheet alone; nothing is filled in that the sheet does not say.</summary>
    public static Decision Evaluate(SheetRow row, IReadOnlyList<Location> locations, DateOnly today)
    {
        var problems = new List<string>();
        Decision Stop(Outcome outcome) => new(row, outcome, string.Join("؛ ", problems));

        if (row.Name.Length is 0 or > 150) problems.Add(row.Name.Length == 0 ? "نام خالی است" : "نام بیش از ۱۵۰ حرف");
        if (!Kinds.TryGetValue(Normalize(row.Kind), out var kind)) problems.Add($"نوع نامعتبر «{row.Kind}» (دوا، فریم، عدسیه یا عمومی)");
        if (row.Unit.Length is 0 or > 30) problems.Add(row.Unit.Length == 0 ? "واحد خالی است" : "واحد بیش از ۳۰ حرف");
        if (row.Batch.Length > 50) problems.Add("شماره بچ بیش از ۵۰ حرف");
        if (row.Form.Length > 80) problems.Add("شکل بیش از ۸۰ حرف");
        if (row.Dose.Length > 45) problems.Add("دوز بیش از ۴۵ حرف");

        var quantity = Number(row.Quantity);
        if (quantity is not > 0 || decimal.Round(quantity.Value, 3) != quantity) problems.Add($"تعداد نامعتبر «{row.Quantity}»");

        decimal? Price(string text, string label)
        {
            if (text.Length == 0) return null;
            var value = Number(text);
            if (value is not >= 0 || decimal.Round(value.Value, 2) != value || value > 100_000_000) problems.Add($"{label} نامعتبر «{text}»");
            return value;
        }

        var cost = Price(row.PurchasePrice, "قیمت خرید");
        var sale = Price(row.SalePrice, "قیمت فروش");

        var location = locations.FirstOrDefault(l => Normalize(l.Code) == Normalize(row.Location) || Normalize(l.Name) == Normalize(row.Location));
        if (location is null) problems.Add($"محل نامعلوم «{row.Location}» ({string.Join("، ", locations.Select(l => l.Name))})");

        DateOnly? expiry = null;
        if (row.Expiry.Length > 0)
        {
            expiry = Date(row.Expiry);
            if (expiry is null) problems.Add($"تاریخ انقضا نامعتبر «{row.Expiry}» (میلادی: 2028-11-30 یا 2028-11)");
        }

        if (problems.Count > 0)
        {
            return Stop(Outcome.Error);
        }

        // Missing on the sheet: kept out rather than guessed.
        if (kind == ProductKind.Drug && row.Dose.Length == 0) problems.Add($"دوز ذکر نشده (اگر دوز ندارد «{NoDose}» بنویسید)");
        if (kind == ProductKind.Drug && expiry is null) problems.Add("تاریخ انقضای دوا ذکر نشده");
        if (expiry < today) problems.Add($"منقضی: {expiry:yyyy-MM-dd}");
        if (problems.Count > 0)
        {
            return Stop(Outcome.Held);
        }

        var name = row.Dose.Length == 0 || row.Dose == NoDose ? row.Name : $"{row.Name} {row.Dose}";
        var item = new Parsed(name, kind, row.Unit, quantity!.Value, cost, sale, row.Batch, expiry, location!.Code, row.Form.Length == 0 ? null : row.Form);
        return new(row, Outcome.Import, "", item, Key(item));
    }

    /// <summary>What makes a row the same row on a second run: item, unit, location, batch and expiry, not the quantity or prices.</summary>
    public static string Key(Parsed item)
    {
        var text = string.Join('|', Normalize(item.ProductName), Normalize(item.Unit), item.LocationCode, Normalize(item.Batch), item.Expiry?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        return $"[sheet:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16]}]";
    }

    /// <summary>One product per name and unit, so the same item in several locations or batches shares it.</summary>
    public static string ProductCode(Parsed item) =>
        $"OS-{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{Normalize(item.ProductName)}|{Normalize(item.Unit)}")))[..12]}";

    private static string Clean(string text) =>
        string.Join(' ', text.Replace('ي', 'ی').Replace('ك', 'ک').Replace(' ', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>For comparing: case, spaces and the half-space do not count.</summary>
    public static string Normalize(string? text) =>
        Clean(text ?? "").Replace("‌", "").Replace(" ", "").ToLowerInvariant();

    private static string Digits(string text)
    {
        var b = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            b.Append(c switch
            {
                >= '۰' and <= '۹' => (char)('0' + c - '۰'),
                >= '٠' and <= '٩' => (char)('0' + c - '٠'),
                '٫' => '.',
                '٬' => ',',
                _ => c,
            });
        }

        return b.ToString().Trim();
    }

    public static decimal? Number(string text) =>
        decimal.TryParse(Digits(text).Replace(",", ""), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>Gregorian only, year first or month/year. A month-only date is the last day of that month, as on the paper sheet.</summary>
    public static DateOnly? Date(string text)
    {
        var t = Digits(text).Replace('/', '-').Replace('.', '-');
        if (DateOnly.TryParseExact(t, ["yyyy-M-d", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) && day.Year >= 2000)
        {
            return day;
        }

        var parts = t.Split('-');
        if (parts.Length == 2 && parts.All(p => p.All(char.IsAsciiDigit) && p.Length > 0))
        {
            var (year, month) = parts[0].Length == 4 ? (int.Parse(parts[0]), int.Parse(parts[1])) : (int.Parse(parts[1]), int.Parse(parts[0]));
            if (year is >= 2000 and <= 2100 && month is >= 1 and <= 12)
            {
                return new DateOnly(year, month, DateTime.DaysInMonth(year, month));
            }
        }

        return null;
    }

    // ── Run ──────────────────────────────────────────────────────────────

    public sealed record Result(string Database, IReadOnlyList<Decision> Decisions, bool Applied)
    {
        public int Count(Outcome outcome) => Decisions.Count(d => d.Outcome == outcome);
    }

    public static async Task<Result> RunAsync(IServiceProvider services, string path, string? userName, bool apply, bool backupTaken, int? expectImport,
        string? reportPath, TextWriter output, CancellationToken ct = default)
    {
        if (services.GetRequiredService<SyncSwitch>().ReadOnly)
        {
            throw new InvalidOperationException("This is the online read-only copy. Stock is imported on the hospital server only.");
        }

        var db = services.GetRequiredService<AppDbContext>();
        var target = new MySqlConnectionStringBuilder(db.Database.GetConnectionString());
        var database = $"{target.Database} on {target.Server}:{target.Port}";
        output.WriteLine($"Target database: {database}");

        if (await DatabaseSafety.SchemaProblemAsync(db, ct) is { } problem)
        {
            throw new InvalidOperationException($"{problem} Nothing was changed.");
        }

        if (!await db.LedgerAccounts.AnyAsync(a => a.Code == Accounts.OpeningBalance, ct))
        {
            throw new InvalidOperationException($"Ledger account {Accounts.OpeningBalance} is missing. Install the matching version with update.ps1 first. Nothing was changed.");
        }

        var rows = Read(path);
        var decisions = await DecideAsync(db, rows, ct);
        var result = new Result(database, decisions, false);

        foreach (var d in decisions)
        {
            output.WriteLine(string.Join('\t', d.Row.Line, d.Outcome, d.Row.Name, d.Row.Dose, d.Row.Quantity, d.Row.Unit, d.Item?.LocationCode ?? d.Row.Location, d.Item?.Expiry?.ToString("yyyy-MM-dd"), d.Reason));
        }

        var value = decisions.Where(d => d.Outcome == Outcome.Import).Sum(d => Value(d.Item!));
        output.WriteLine($"Rows {decisions.Count}: import {result.Count(Outcome.Import)}, already imported {result.Count(Outcome.AlreadyImported)}, held {result.Count(Outcome.Held)}, errors {result.Count(Outcome.Error)}. Value to post: {value:0.00}.");
        if (reportPath is not null)
        {
            WriteReport(reportPath, decisions);
        }

        if (!apply)
        {
            output.WriteLine("Dry run: nothing was changed.");
            return result;
        }

        if (!backupTaken)
        {
            throw new InvalidOperationException($"Take and verify a backup first, then add {DatabaseSafety.BackupTakenFlag}. Nothing was changed.");
        }

        var toImport = decisions.Where(d => d.Outcome == Outcome.Import).ToList();
        if (expectImport is null || expectImport != toImport.Count)
        {
            throw new InvalidOperationException($"The preview showed {expectImport?.ToString() ?? "nothing"}, now {toImport.Count} rows would be imported. Run the preview again. Nothing was changed.");
        }

        if (toImport.Count == 0)
        {
            output.WriteLine("Nothing to import.");
            return result with { Applied = true };
        }

        var user = string.IsNullOrWhiteSpace(userName) ? null : await services.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Users.AppUser>>().FindByNameAsync(userName);
        if (user is null || !user.IsActive)
        {
            throw new InvalidOperationException("Set ALSHIFA_IMPORT_USER to an active account that records this stock. Nothing was changed.");
        }

        var accessor = services.GetRequiredService<IHttpContextAccessor>();
        var previous = accessor.HttpContext;
        accessor.HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())], "operator")) };
        try
        {
            await ApplyAsync(services, db, rows, toImport, Path.GetFileName(path), ct);
        }
        finally
        {
            accessor.HttpContext = previous;
        }

        output.WriteLine($"Committed {toImport.Count} rows to {database}. Posted {value:0.00} to inventory.");
        return result with { Applied = true };
    }

    private static decimal Value(Parsed item) =>
        item.UnitCost is { } cost ? decimal.Round(item.Quantity * cost, 2, MidpointRounding.AwayFromZero) : 0;

    /// <summary>Checks every row against the sheet, the other rows and what the database already holds.</summary>
    public static async Task<List<Decision>> DecideAsync(AppDbContext db, List<SheetRow> rows, CancellationToken ct)
    {
        var locations = await db.StockLocations.AsNoTracking().Select(l => new Location(l.Code, l.Name)).ToListAsync(ct);
        var today = KabulClock.Today;
        var decisions = rows.Select(r => Evaluate(r, locations, today)).ToList();

        void Hold(Func<Decision, bool> which, Func<Decision, string> reason)
        {
            for (var i = 0; i < decisions.Count; i++)
            {
                if (decisions[i].Outcome == Outcome.Import && which(decisions[i]))
                {
                    decisions[i] = decisions[i] with { Outcome = Outcome.Held, Reason = reason(decisions[i]) };
                }
            }
        }

        // The same row twice in the file: neither is taken, someone decides which count is right.
        var repeated = decisions.Where(d => d.Key is not null).GroupBy(d => d.Key!).Where(g => g.Count() > 1)
            .ToDictionary(g => g.Key, g => string.Join("، ", g.Select(d => d.Row.Line)));
        Hold(d => repeated.ContainsKey(d.Key!), d => $"ردیف تکراری در فایل (ردیف‌های {repeated[d.Key!]})");

        // One item, one sale price and one kind across the file.
        var conflicting = decisions.Where(d => d.Item is not null).GroupBy(d => ProductCode(d.Item!))
            .Where(g => g.Select(d => d.Item!.SalePrice).Distinct().Count() > 1 || g.Select(d => d.Item!.Kind).Distinct().Count() > 1 || g.Select(d => d.Item!.Form).Distinct().Count() > 1)
            .Select(g => g.Key).ToHashSet();
        Hold(d => conflicting.Contains(ProductCode(d.Item!)), _ => "قیمت فروش، نوع یا شکل این جنس در ردیف‌های فایل یکسان نیست");

        // Recorded by an earlier run: the same quantity is done, another quantity needs a person.
        var recorded = (await db.StockMovements.AsNoTracking()
                .Where(m => m.SourceType == OpeningStockImport.SourceType && m.Reason!.StartsWith("[sheet:"))
                .Select(m => new { m.Reason, m.Quantity }).ToListAsync(ct))
            .GroupBy(m => m.Reason![..(m.Reason!.IndexOf(']') + 1)])
            .ToDictionary(g => g.Key, g => g.First().Quantity);
        for (var i = 0; i < decisions.Count; i++)
        {
            var d = decisions[i];
            if (d.Key is not null && recorded.TryGetValue(d.Key, out var quantity) && d.Outcome is Outcome.Import or Outcome.Held && !repeated.ContainsKey(d.Key))
            {
                decisions[i] = quantity == d.Item!.Quantity
                    ? d with { Outcome = Outcome.AlreadyImported, Reason = "قبلاً ثبت شده" }
                    : d with { Outcome = Outcome.Held, Reason = $"قبلاً با تعداد {quantity:0.###} ثبت شده؛ اصلاح از صفحه گردش موجودی" };
            }
        }

        // Items already in the system: the same name and unit is the same item; anything merely similar is left to a person.
        var products = await db.Products.AsNoTracking().Select(p => new { p.Id, p.Code, p.Name, p.Unit, p.Kind, p.SalePrice }).ToListAsync(ct);
        foreach (var d in decisions.Where(d => d.Outcome == Outcome.Import).ToList())
        {
            var item = d.Item!;
            var code = ProductCode(item);
            var same = products.Where(p => p.Code == code || (Normalize(p.Name) == Normalize(item.ProductName) && Normalize(p.Unit) == Normalize(item.Unit))).ToList();
            var similar = products.Where(p => !p.Code.StartsWith("OS-") && !same.Contains(p) && Normalize(p.Name).Contains(Normalize(d.Row.Name))).ToList();
            var reason = same.Count > 1 ? $"چند جنس هم‌نام در سیستم: {string.Join("، ", same.Select(p => p.Code))}"
                : similar.Count > 0 ? $"جنس مشابه در سیستم؛ تطبیق دستی لازم: {string.Join("، ", similar.Take(5).Select(p => $"{p.Code} {p.Name} ({p.Unit})"))}"
                : same.SingleOrDefault() is { } existing && existing.Kind != item.Kind ? $"نوع جنس با سیستم فرق دارد ({existing.Code}: {existing.Kind})"
                : same.SingleOrDefault() is { } priced && priced.SalePrice > 0 && item.SalePrice is { } price && price != priced.SalePrice ? $"قیمت فروش با سیستم فرق دارد ({priced.SalePrice:0.##})"
                : null;
            if (reason is not null)
            {
                Hold(x => x == d, _ => reason);
            }
        }

        // A batch number already in stock for this item and location came in some other way; adding it again would count it twice.
        var lots = decisions.Where(d => d.Outcome == Outcome.Import && d.Item!.Batch.Length > 0).Select(d => d.Item!.Batch).Distinct().ToList();
        if (lots.Count > 0)
        {
            var existing = await db.StockBatches.AsNoTracking().Where(b => lots.Contains(b.LotNumber))
                .Select(b => new { b.LotNumber, b.Product!.Name, b.Product.Unit, Location = b.Location!.Code }).ToListAsync(ct);
            Hold(d => d.Item!.Batch.Length > 0 && existing.Any(b => Normalize(b.LotNumber) == Normalize(d.Item.Batch) && Normalize(b.Name) == Normalize(d.Item.ProductName)
                    && Normalize(b.Unit) == Normalize(d.Item.Unit) && b.Location == d.Item.LocationCode),
                _ => "این بچ برای همین جنس و محل از قبل در سیستم است");
        }

        return decisions;
    }

    private static async Task ApplyAsync(IServiceProvider services, AppDbContext db, List<SheetRow> rows, List<Decision> preview, string fileName, CancellationToken ct)
    {
        var stock = services.GetRequiredService<StockService>();
        var ledger = services.GetRequiredService<LedgerService>();
        var audit = services.GetRequiredService<AuditService>();
        var outbox = services.GetRequiredService<Outbox>();

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // A second run started at the same time waits here, then sees these rows as recorded.
        await db.Database.ExecuteSqlRawAsync("SELECT Id FROM StockLocations ORDER BY Id FOR UPDATE", ct);
        var toImport = (await DecideAsync(db, rows, ct)).Where(d => d.Outcome == Outcome.Import).ToList();
        if (!toImport.Select(d => d.Key).SequenceEqual(preview.Select(d => d.Key)))
        {
            throw new InvalidOperationException("The database changed since the preview. Run the preview again. Nothing was changed.");
        }

        var locationIds = await db.StockLocations.ToDictionaryAsync(l => l.Code, l => l.Id, ct);
        var products = await db.Products.AsNoTracking().Select(p => new { p.Id, p.Code, p.Name, p.Unit }).ToListAsync(ct);
        var productIds = new Dictionary<string, int>();
        var posted = 0m;
        var batchIds = new Dictionary<string, long>();
        foreach (var d in toImport)
        {
            var item = d.Item!;
            var code = ProductCode(item);
            if (!productIds.TryGetValue(code, out var productId))
            {
                // Matched exactly as the preview matched it; the preview already stopped any row with more than one match.
                var existing = products.SingleOrDefault(p => p.Code == code || (Normalize(p.Name) == Normalize(item.ProductName) && Normalize(p.Unit) == Normalize(item.Unit)))?.Id;
                if (existing is null)
                {
                    var product = new Product
                    {
                        Code = code,
                        Name = item.ProductName,
                        Kind = item.Kind,
                        Category = item.Form,
                        Unit = item.Unit,
                        SalePrice = item.SalePrice ?? 0,
                        IsActive = item.SalePrice > 0,
                    };
                    db.Products.Add(product);
                    await db.SaveChangesAsync(ct);
                    audit.Add("ProductCreated", nameof(Product), product.Id.ToString(), $"موجودی اولیه از {fileName}{(product.IsActive ? "" : "؛ غیرفعال تا تعیین قیمت فروش")}");
                    existing = product.Id;
                }

                productId = productIds[code] = existing.Value;
            }

            var batch = new StockBatch { ProductId = productId, LocationId = locationIds[item.LocationCode], LotNumber = item.Batch, ExpiryDate = item.Expiry, Quantity = item.Quantity, UnitCost = item.UnitCost ?? 0 };
            db.StockBatches.Add(batch);
            await db.SaveChangesAsync(ct);

            var file = fileName.Length > 60 ? fileName[..60] : fileName;
            var reason = $"{d.Key} موجودی اولیه؛ {file} ردیف {d.Row.Line}{(item.UnitCost is null ? "؛ قیمت خرید نامعلوم" : "")}";
            stock.AddMovement(batch, MovementKind.Adjustment, batch.Quantity, OpeningStockImport.SourceType, batch.Id, reason);
            await db.SaveChangesAsync(ct);

            var value = Value(item);
            if (value > 0)
            {
                var movementId = await db.StockMovements.Where(m => m.BatchId == batch.Id).Select(m => m.Id).SingleAsync(ct);
                ledger.Post(LedgerKind, nameof(StockMovement), movementId, reason, (Accounts.Inventory, value, 0), (Accounts.OpeningBalance, 0, value));
                posted += value;
            }

            audit.Add("OpeningStockImported", nameof(StockBatch), batch.Id.ToString(), reason);
            batchIds[d.Key!] = batch.Id;
        }

        outbox.Add("InventoryChanged", new { source = "opening-sheet" }, Groups.Pharmacy, Groups.Optical, Groups.Inventory);
        await db.SaveChangesAsync(ct);

        // Read back before committing: each batch must hold exactly what its row says, and the ledger exactly its value.
        db.ChangeTracker.Clear();
        foreach (var d in toImport)
        {
            var id = batchIds[d.Key!];
            var check = await db.StockBatches.AsNoTracking().Where(b => b.Id == id)
                .Select(b => new { b.Quantity, b.ExpiryDate, b.UnitCost, b.LotNumber, b.Product!.Unit, Location = b.Location!.Code, Moved = db.StockMovements.Where(m => m.BatchId == b.Id).Sum(m => m.Quantity) })
                .SingleAsync(ct);
            var item = d.Item!;
            if (check.Quantity != item.Quantity || check.Moved != item.Quantity || check.ExpiryDate != item.Expiry || check.UnitCost != (item.UnitCost ?? 0)
                || check.LotNumber != item.Batch || check.Location != item.LocationCode || Normalize(check.Unit) != Normalize(item.Unit))
            {
                throw new InvalidOperationException($"Read-back of row {d.Row.Line} does not match the sheet. Nothing was changed.");
            }
        }

        var movementIds = await db.StockMovements.Where(m => batchIds.Values.Contains(m.BatchId)).Select(m => m.Id).ToListAsync(ct);
        var ledgerTotal = await (from t in db.LedgerTransactions
                                 join e in db.LedgerEntries on t.Id equals e.TransactionId
                                 where t.Kind == LedgerKind && t.SourceType == nameof(StockMovement) && movementIds.Contains(t.SourceId) && e.AccountCode == Accounts.Inventory
                                 select (decimal?)e.Debit).SumAsync(ct) ?? 0;
        if (ledgerTotal != posted)
        {
            throw new InvalidOperationException("Read-back of the ledger does not match the sheet. Nothing was changed.");
        }

        await tx.CommitAsync(ct);
    }

    private static void WriteReport(string path, IEnumerable<Decision> decisions)
    {
        static string Csv(string? s) => s is null ? "" : s.Contains(',') || s.Contains('"') || s.Contains('\n') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
        var outcome = new Dictionary<Outcome, string>
        {
            [Outcome.Import] = "ثبت می‌شود", [Outcome.AlreadyImported] = "قبلاً ثبت شده", [Outcome.Held] = "نگه داشته شد", [Outcome.Error] = "خطا",
        };
        var lines = new List<string> { "ردیف,نتیجه,دلیل,نام,دوز,واحد,تعداد,قیمت خرید,قیمت فروش,شماره بچ,تاریخ انقضا,محل,نوع,ارزش" };
        lines.AddRange(decisions.Select(d => string.Join(',', d.Row.Line, outcome[d.Outcome], Csv(d.Reason), Csv(d.Row.Name), Csv(d.Row.Dose), Csv(d.Row.Unit), Csv(d.Row.Quantity),
            Csv(d.Row.PurchasePrice), Csv(d.Row.SalePrice), Csv(d.Row.Batch), Csv(d.Item?.Expiry?.ToString("yyyy-MM-dd") ?? d.Row.Expiry), Csv(d.Row.Location), Csv(d.Row.Kind),
            d.Item is null ? "" : Value(d.Item).ToString("0.00", CultureInfo.InvariantCulture))));
        File.WriteAllLines(path, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }
}
