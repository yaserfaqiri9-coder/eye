using System.Security.Claims;
using System.Text.Json;
using AlShifa.Api.Features.Sync;
using AlShifa.Api.Features.Users;
using AlShifa.Api.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;

namespace AlShifa.Api.Features.Inventory;

/// <summary>
/// Operator command: brings the clinic's hand-counted opening stock (inventory_staging.json) into the pharmacy.
///   ALSHIFA_IMPORT_USER=admin AlShifa.Api --import-opening-stock path\inventory_staging.json                       (dry run, changes nothing)
///   ALSHIFA_IMPORT_USER=admin AlShifa.Api --import-opening-stock path\inventory_staging.json --apply --backup-taken
/// Only rows with no open question become stock. Each row is checked on its own; the sheet's approval flag is not set by this command.
/// A product made here is inactive (not for sale) and its stock carries no cost and no ledger entry until the purchase price and currency are known.
/// A month-only expiry is stored as the last day of that month. Each row is recorded once: running again adds nothing.
/// </summary>
public static class OpeningStockImport
{
    public const string Command = "--import-opening-stock";
    public const string ApplyFlag = "--apply";
    public const string SourceType = "OpeningStock";
    public const string LocationCode = "pharmacy";

    private static readonly HashSet<string> Units = ["عدد", "دانه", "بکس", "قطی", "بسته", "جوره"];

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public sealed record StagingFile(string PreparedOn, int RecordCount, List<StagingRow> Rows);

    public sealed record StagingRow(
        string SourceRecordId,
        string ItemGroup,
        string? FormRaw,
        string NameRaw,
        string? StrengthRaw,
        string? StrengthKind,
        string? QuantityRaw,
        decimal? QuantityToImport,
        string? QuantityUnit,
        decimal? BaseQuantityExplicit,
        string? ExpiryRaw,
        int? ExpiryMonth,
        int? ExpiryYear,
        int? ExpiryDay,
        string? FieldIssues,
        string ImportAction);

    public enum Outcome { Import, AlreadyImported, Held, Excluded }

    public sealed record Decision(StagingRow Row, Outcome Outcome, string Reason, DateOnly? Expiry = null);

    public sealed record Result(string Database, string DatasetId, IReadOnlyList<Decision> Decisions, bool Applied)
    {
        public int Count(Outcome outcome) => Decisions.Count(d => d.Outcome == outcome);
    }

    public static string DatasetId(StagingFile file) => $"opening-{file.PreparedOn}";

    public static string ProductCode(StagingFile file, StagingRow row) => $"OS-{file.PreparedOn.Replace("-", "")}-{row.SourceRecordId}";

    /// <summary>Starts every movement's reason, so a second run finds the rows the first one recorded.</summary>
    public static string Key(StagingFile file, StagingRow row) => $"[{DatasetId(file)}/{row.SourceRecordId}]";

    public static StagingFile Read(string path)
    {
        var file = JsonSerializer.Deserialize<StagingFile>(File.ReadAllText(path), Json) ?? throw new InvalidOperationException("The file is empty.");
        if (file.Rows.Count != file.RecordCount)
        {
            throw new InvalidOperationException($"The file says {file.RecordCount} rows but holds {file.Rows.Count}. Nothing was changed.");
        }

        var duplicate = file.Rows.GroupBy(r => r.SourceRecordId).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"source_record_id {duplicate.Key} appears twice. Nothing was changed.");
        }

        return file;
    }

    /// <summary>Decides one row from the sheet alone. Anything unclear on the sheet stops the row.</summary>
    public static Decision Evaluate(StagingRow row, DateOnly today)
    {
        if (row.ImportAction == "exclude")
        {
            return new(row, Outcome.Excluded, "ردیف خط‌خورده");
        }

        if (row.ImportAction != "stage_only")
        {
            return new(row, Outcome.Held, $"import_action ناشناخته: {row.ImportAction}");
        }

        if (row.QuantityToImport is not > 0)
        {
            return new(row, Outcome.Held, $"تعداد مبهم: {row.QuantityRaw}");
        }

        if (row.ItemGroup != "medicine")
        {
            return new(row, Outcome.Held, $"مواد عملیات‌خانه؛ {row.FieldIssues}");
        }

        if (string.IsNullOrWhiteSpace(row.StrengthRaw) || row.StrengthKind != "medicine_strength")
        {
            return new(row, Outcome.Held, string.IsNullOrWhiteSpace(row.FieldIssues) ? "دوز/غلظت ذکر نشده" : row.FieldIssues);
        }

        if (!string.IsNullOrWhiteSpace(row.FieldIssues))
        {
            return new(row, Outcome.Held, row.FieldIssues);
        }

        if (row.QuantityUnit is null || !Units.Contains(row.QuantityUnit) || row.BaseQuantityExplicit is not null)
        {
            return new(row, Outcome.Held, $"واحد شمارش نامعلوم: {row.QuantityUnit}");
        }

        if (row.ExpiryMonth is not (>= 1 and <= 12) || row.ExpiryYear is not (>= 2000 and <= 2100) || row.ExpiryDay is not null)
        {
            return new(row, Outcome.Held, $"تاریخ انقضا نامعلوم: {row.ExpiryRaw}");
        }

        var expiry = new DateOnly(row.ExpiryYear.Value, row.ExpiryMonth.Value, DateTime.DaysInMonth(row.ExpiryYear.Value, row.ExpiryMonth.Value));
        return expiry < today
            ? new(row, Outcome.Held, $"منقضی: {row.ExpiryRaw}")
            : new(row, Outcome.Import, "", expiry);
    }

    public static string ProductName(StagingRow row) => $"{row.NameRaw.Trim()} {row.StrengthRaw!.Trim()}";

    public static async Task<Result> RunAsync(IServiceProvider services, string path, string? userName, bool apply, bool backupTaken, TextWriter output, CancellationToken ct = default)
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

        var file = Read(path);
        var datasetId = DatasetId(file);
        var today = KabulClock.Today;
        var decisions = file.Rows.Select(r => Evaluate(r, today)).ToList();

        var keys = decisions.ToDictionary(d => d.Row.SourceRecordId, d => Key(file, d.Row));
        var recorded = (await db.StockMovements.AsNoTracking()
                .Where(m => m.SourceType == SourceType && m.Reason!.StartsWith($"[{datasetId}/"))
                .Select(m => m.Reason!).ToListAsync(ct))
            .Select(r => r[..(r.IndexOf(']') + 1)]).ToHashSet();

        var locationId = await db.StockLocations.Where(l => l.Code == LocationCode).Select(l => (int?)l.Id).SingleOrDefaultAsync(ct)
            ?? throw new InvalidOperationException($"Stock location '{LocationCode}' does not exist. Nothing was changed.");

        // An item the clinic already has is matched only on the same name with strength, form and unit; a similar name stops the row.
        var products = await db.Products.AsNoTracking().Select(p => new { p.Id, p.Code, p.Name, p.Category, p.Unit }).ToListAsync(ct);
        var productFor = new Dictionary<string, int?>();
        var ownCodes = $"OS-{file.PreparedOn.Replace("-", "")}-";
        for (var i = 0; i < decisions.Count; i++)
        {
            var d = decisions[i];
            if (d.Outcome != Outcome.Import)
            {
                continue;
            }

            if (recorded.Contains(keys[d.Row.SourceRecordId]))
            {
                decisions[i] = d with { Outcome = Outcome.AlreadyImported, Reason = "قبلاً ثبت شده" };
                continue;
            }

            var code = ProductCode(file, d.Row);
            var name = ProductName(d.Row);
            var own = products.SingleOrDefault(p => p.Code == code);
            var same = products.Where(p => p.Code != code && Same(p.Name, name) && Same(p.Category, d.Row.FormRaw) && Same(p.Unit, d.Row.QuantityUnit)).ToList();
            var similar = products.Where(p => !p.Code.StartsWith(ownCodes) && !same.Contains(p) && p.Name.Contains(d.Row.NameRaw.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            if (same.Count > 1 || similar.Count > 0)
            {
                decisions[i] = d with { Outcome = Outcome.Held, Reason = $"جنس هم‌نام در سیستم؛ تطبیق دستی لازم: {string.Join("، ", same.Concat(similar).Select(p => $"{p.Code} {p.Name} ({p.Category}، {p.Unit})"))}" };
                continue;
            }

            productFor[d.Row.SourceRecordId] = own?.Id ?? same.SingleOrDefault()?.Id;
        }

        foreach (var d in decisions)
        {
            output.WriteLine(string.Join('\t', d.Row.SourceRecordId, d.Outcome, d.Row.NameRaw, d.Row.StrengthRaw, d.Row.QuantityToImport, d.Row.QuantityUnit, d.Expiry?.ToString("yyyy-MM-dd"), d.Reason));
        }

        var result = new Result(database, datasetId, decisions, false);
        output.WriteLine($"Rows {decisions.Count}: import {result.Count(Outcome.Import)}, already imported {result.Count(Outcome.AlreadyImported)}, held {result.Count(Outcome.Held)}, crossed out {result.Count(Outcome.Excluded)}.");

        if (!apply)
        {
            output.WriteLine("Dry run: nothing was changed.");
            return result;
        }

        if (!backupTaken)
        {
            throw new InvalidOperationException($"Take a backup first (deploy\\windows\\backup.ps1), then add {DatabaseSafety.BackupTakenFlag}. Nothing was changed.");
        }

        var toImport = decisions.Where(d => d.Outcome == Outcome.Import).ToList();
        if (toImport.Count == 0)
        {
            output.WriteLine("Nothing to import.");
            return result with { Applied = true };
        }

        var userId = await ImportUserAsync(services, userName);
        var accessor = services.GetRequiredService<IHttpContextAccessor>();
        var previous = accessor.HttpContext;
        accessor.HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "operator")) };
        try
        {
            var stock = services.GetRequiredService<StockService>();
            var audit = services.GetRequiredService<AuditService>();
            var outbox = services.GetRequiredService<Outbox>();

            await using var tx = await db.Database.BeginTransactionAsync(ct);

            // A second run started at the same time waits here and then finds these rows recorded.
            await db.Database.ExecuteSqlRawAsync("SELECT Id FROM StockLocations WHERE Id = {0} FOR UPDATE", [locationId], ct);
            var nowRecorded = await db.StockMovements.Where(m => m.SourceType == SourceType && m.Reason!.StartsWith($"[{datasetId}/")).CountAsync(ct);
            if (nowRecorded != recorded.Count)
            {
                throw new InvalidOperationException("Another import of this sheet finished meanwhile. Run the dry run again. Nothing was changed.");
            }

            var batchIds = new Dictionary<string, long>();
            foreach (var d in toImport)
            {
                var row = d.Row;
                var productId = productFor[row.SourceRecordId];
                if (productId is null)
                {
                    var product = new Product
                    {
                        Code = ProductCode(file, row),
                        Name = ProductName(row),
                        Kind = ProductKind.Drug,
                        Category = row.FormRaw,
                        Unit = row.QuantityUnit!,
                        IsActive = false,
                    };
                    db.Products.Add(product);
                    await db.SaveChangesAsync(ct);
                    audit.Add("ProductCreated", nameof(Product), product.Id.ToString(), $"{Key(file, row)} غیرفعال تا تعیین قیمت");
                    productId = product.Id;
                }

                var batch = new StockBatch { ProductId = productId.Value, LocationId = locationId, LotNumber = "", ExpiryDate = d.Expiry, Quantity = row.QuantityToImport!.Value, UnitCost = 0 };
                db.StockBatches.Add(batch);
                await db.SaveChangesAsync(ct);

                var reason = $"{Key(file, row)} موجودی اولیه؛ برگه: {row.QuantityRaw}، انقضا {row.ExpiryRaw} (ماه/سال، ثبت به آخر ماه)؛ قیمت و ارز نامعلوم";
                stock.AddMovement(batch, MovementKind.Adjustment, batch.Quantity, SourceType, batch.Id, reason);
                audit.Add("OpeningStockImported", nameof(StockBatch), batch.Id.ToString(), reason);
                batchIds[row.SourceRecordId] = batch.Id;
            }

            outbox.Add("InventoryChanged", new { dataset = datasetId }, Groups.Pharmacy, Groups.Optical, Groups.Inventory);
            await db.SaveChangesAsync(ct);

            // Read back before committing: each recorded row must show exactly the sheet's quantity, unit and expiry.
            db.ChangeTracker.Clear();
            foreach (var d in toImport)
            {
                var id = batchIds[d.Row.SourceRecordId];
                var check = await db.StockBatches.AsNoTracking().Where(b => b.Id == id)
                    .Select(b => new { b.Quantity, b.ExpiryDate, b.UnitCost, b.Product!.Unit, Moved = db.StockMovements.Where(m => m.BatchId == b.Id).Sum(m => m.Quantity) })
                    .SingleAsync(ct);
                if (check.Quantity != d.Row.QuantityToImport || check.Moved != check.Quantity || check.ExpiryDate != d.Expiry || check.Unit != d.Row.QuantityUnit || check.UnitCost != 0)
                {
                    throw new InvalidOperationException($"Read-back of {d.Row.SourceRecordId} does not match the sheet. Nothing was changed.");
                }
            }

            await tx.CommitAsync(ct);
            output.WriteLine($"Committed {toImport.Count} rows to {database}.");
        }
        finally
        {
            accessor.HttpContext = previous;
        }

        return result with { Applied = true };
    }

    private static bool Same(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static async Task<long> ImportUserAsync(IServiceProvider services, string? userName)
    {
        var user = string.IsNullOrWhiteSpace(userName) ? null : await services.GetRequiredService<UserManager<AppUser>>().FindByNameAsync(userName);
        return user?.Id ?? throw new InvalidOperationException("Set ALSHIFA_IMPORT_USER to the account that records this stock. Nothing was changed.");
    }
}
