using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Finance;
using AlShifa.Api.Features.Inventory;
using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static AlShifa.Api.Features.Inventory.OpeningStockSheet;

namespace AlShifa.Tests;

/// <summary>The typed opening stock sheet: only complete rows become stock, once, with their value in the ledger.</summary>
[Collection("api")]
public sealed class OpeningStockSheetTests(ApiFixture api)
{
    private const string Header = "نام,دوز,واحد,تعداد,قیمت خرید,قیمت فروش,شماره بچ,تاریخ انقضا,محل,نوع,شکل";

    private static string Unique() => Guid.NewGuid().ToString("N")[..8];

    private static string Csv(params string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(), $"stock-{Unique()}.csv");
        File.WriteAllText(path, string.Join("\r\n", [Header, .. lines]), new UTF8Encoding(true));
        return path;
    }

    private async Task<Result> Run(string path, string user, bool apply, int? expect = null)
    {
        using var scope = api.Services.CreateScope();
        if (apply && expect is null)
        {
            expect = (await RunAsync(scope.ServiceProvider, path, user, false, false, null, null, TextWriter.Null)).Count(Outcome.Import);
        }

        return await RunAsync(scope.ServiceProvider, path, user, apply, backupTaken: true, expect, null, TextWriter.Null);
    }

    private async Task<T> InDb<T>(Func<AppDbContext, Task<T>> read)
    {
        using var scope = api.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private async Task<string> ImportUser()
    {
        var user = $"imp{Unique()}";
        await api.CreateUserAsync(user, Roles.Inventory);
        return user;
    }

    private static readonly Location[] Locations = [new("central", "گدام مرکزی"), new("pharmacy", "دواخانه"), new("optical", "بخش عینک"), new("surgery", "گدام عملیات‌خانه")];

    private static SheetRow Row(string name = "Timolol", string dose = "0.5%", string unit = "دانه", string quantity = "10", string cost = "50", string sale = "80",
        string batch = "B1", string expiry = "2028-11", string location = "دواخانه", string kind = "دوا") =>
        new(2, name, dose, unit, quantity, cost, sale, batch, expiry, location, kind, "قطره چشمی");

    [Fact]
    public void Unclear_or_missing_values_are_not_guessed()
    {
        var today = new DateOnly(2026, 11, 11);
        var clear = Evaluate(Row(quantity: "۱۲", expiry: "02/2028", location: "گدام عملیات‌خانه"), Locations, today);
        Assert.Equal(Outcome.Import, clear.Outcome);
        Assert.Equal((12m, new DateOnly(2028, 2, 29), "surgery"), (clear.Item!.Quantity, clear.Item.Expiry, clear.Item.LocationCode));

        Assert.Equal(Outcome.Error, Evaluate(Row(location: "انبار"), Locations, today).Outcome);
        Assert.Equal(Outcome.Error, Evaluate(Row(kind: ""), Locations, today).Outcome);
        Assert.Equal(Outcome.Error, Evaluate(Row(quantity: "چند"), Locations, today).Outcome);
        Assert.Equal(Outcome.Error, Evaluate(Row(quantity: "0"), Locations, today).Outcome);
        Assert.Equal(Outcome.Error, Evaluate(Row(cost: "12.345"), Locations, today).Outcome);
        Assert.Equal(Outcome.Error, Evaluate(Row(expiry: "1405-08"), Locations, today).Outcome);
        Assert.Equal(Outcome.Error, Evaluate(Row(expiry: "11/12/2028"), Locations, today).Outcome);
        Assert.Equal(Outcome.Held, Evaluate(Row(dose: ""), Locations, today).Outcome);
        Assert.Equal(Outcome.Held, Evaluate(Row(expiry: ""), Locations, today).Outcome);
        Assert.Equal(Outcome.Held, Evaluate(Row(expiry: "2026-10"), Locations, today).Outcome);

        // A supply needs neither dose nor expiry, and unknown prices stay unknown.
        var supply = Evaluate(Row("Gauze", dose: "", unit: "بسته", cost: "", sale: "", batch: "", expiry: "", location: "surgery", kind: "عمومی"), Locations, today);
        Assert.Equal((Outcome.Import, null, null, (DateOnly?)null), (supply.Outcome, supply.Item!.UnitCost, supply.Item.SalePrice, supply.Item.Expiry));
        Assert.Equal("Timolol", Evaluate(Row(dose: NoDose), Locations, today).Item!.ProductName);
    }

    [Fact]
    public async Task Rows_become_stock_once_with_their_value_in_the_ledger()
    {
        var name = $"Tim{Unique()}";
        var user = await ImportUser();
        var path = Csv(
            $"{name},0.5%,دانه,10,50,80,LOT-{name},2028-11,دواخانه,دوا,قطره چشمی",
            $"{name},0.5%,دانه,4,50,80,LOT-{name},2028-11,گدام مرکزی,دوا,قطره چشمی",
            $"{name}G,,بسته,20,,,,,گدام عملیات‌خانه,عمومی,",
            $"{name}X,1%,دانه,3,10,20,X,2028-01,دواخانه,دوا,",
            $"{name}X,1%,دانه,5,10,20,X,2028-01,دواخانه,دوا,",
            $"{name}E,1%,دانه,5,10,20,E,2028-01,انبار,دوا,");

        var dry = await Run(path, user, apply: false);
        Assert.Equal((3, 2, 1), (dry.Count(Outcome.Import), dry.Count(Outcome.Held), dry.Count(Outcome.Error)));
        Assert.False(await InDb(db => db.Products.AnyAsync(p => p.Name.StartsWith(name))));

        var first = await Run(path, user, apply: true);
        Assert.True(first.Applied);

        var stock = await InDb(db => db.StockBatches.AsNoTracking().Where(b => b.Product!.Name.StartsWith(name))
            .Select(b => new { b.Product!.Name, b.Product.IsActive, b.Product.SalePrice, b.Quantity, b.UnitCost, b.LotNumber, b.ExpiryDate, Location = b.Location!.Code })
            .OrderBy(b => b.Location).ToListAsync());
        Assert.Equal(3, stock.Count);
        Assert.Equal(($"{name} 0.5%", true, 80m, 4m, 50m, $"LOT-{name}", new DateOnly(2028, 11, 30)), (stock[0].Name, stock[0].IsActive, stock[0].SalePrice, stock[0].Quantity, stock[0].UnitCost, stock[0].LotNumber, stock[0].ExpiryDate));
        Assert.Equal(("pharmacy", 10m), (stock[1].Location, stock[1].Quantity));
        Assert.Equal(($"{name}G", false, 0m, "surgery", (DateOnly?)null), (stock[2].Name, stock[2].IsActive, stock[2].UnitCost, stock[2].Location, stock[2].ExpiryDate));
        Assert.Equal(1, await InDb(db => db.Products.CountAsync(p => p.Name == $"{name} 0.5%")));

        // 10 × 50 + 4 × 50; the supply with no purchase price posts nothing.
        var movements = await InDb(db => db.StockMovements.Where(m => m.Batch!.Product!.Name.StartsWith(name)).Select(m => m.Id).ToListAsync());
        var posted = await InDb(db => (from t in db.LedgerTransactions join e in db.LedgerEntries on t.Id equals e.TransactionId
                                       where t.Kind == LedgerKind && movements.Contains(t.SourceId)
                                       select new { e.AccountCode, e.Debit, e.Credit }).ToListAsync());
        Assert.Equal(700m, posted.Where(e => e.AccountCode == Accounts.Inventory).Sum(e => e.Debit));
        Assert.Equal(700m, posted.Where(e => e.AccountCode == Accounts.OpeningBalance).Sum(e => e.Credit));
        Assert.Equal(4, posted.Count);

        // A second run adds nothing; the same row with another count is stopped, not added.
        var second = await Run(path, user, apply: true);
        Assert.Equal((0, 3), (second.Count(Outcome.Import), second.Count(Outcome.AlreadyImported)));
        var changed = await Run(Csv($"{name},0.5%,دانه,11,50,80,LOT-{name},2028-11,دواخانه,دوا,قطره چشمی"), user, apply: false);
        Assert.Equal(Outcome.Held, changed.Decisions.Single().Outcome);
        Assert.Equal(3, await InDb(db => db.StockBatches.CountAsync(b => b.Product!.Name.StartsWith(name))));
    }

    [Fact]
    public async Task Apply_refuses_without_backup_or_when_the_count_differs_from_the_preview()
    {
        var name = $"Cnt{Unique()}";
        var user = await ImportUser();
        var path = Csv($"{name},1%,دانه,2,5,9,C1,2028-05,دواخانه,دوا,");
        using var scope = api.Services.CreateScope();
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(scope.ServiceProvider, path, user, true, false, 1, null, TextWriter.Null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(scope.ServiceProvider, path, user, true, true, 2, null, TextWriter.Null));
        Assert.False(await InDb(db => db.Products.AnyAsync(p => p.Name.StartsWith(name))));
    }

    [Fact]
    public async Task An_existing_item_or_batch_is_never_merged_by_guess()
    {
        var name = $"Ofl{Unique()}";
        var user = await ImportUser();
        await InDb(async db =>
        {
            db.Products.Add(new Product { Code = $"EX-{Unique()}", Name = $"{name} 0.3% eye drops", Kind = ProductKind.Drug, Unit = "دانه", SalePrice = 100 });
            return await db.SaveChangesAsync();
        });

        var result = await Run(Csv($"{name},0.3%,دانه,5,10,100,L1,2028-05,دواخانه,دوا,"), user, apply: true);
        Assert.Equal(Outcome.Held, result.Decisions.Single().Outcome);
        Assert.Equal(1, await InDb(db => db.Products.CountAsync(p => p.Name.StartsWith(name))));
    }

    [Fact]
    public async Task A_mistaken_row_is_reversed_with_its_ledger_entry()
    {
        var name = $"Rv{Unique()}";
        var user = await ImportUser();
        await Run(Csv($"{name},2%,دانه,5,30,50,R1,2028-05,دواخانه,دوا,"), user, apply: true);
        var movement = await InDb(db => db.StockMovements.Where(m => m.Batch!.Product!.Name.StartsWith(name)).Select(m => m.Id).SingleAsync());

        var inventory = await api.LoginAsync(user);
        Assert.Equal(HttpStatusCode.NoContent, (await inventory.PostAsJsonAsync($"/api/inventory/movements/{movement}/reverse", new ReverseMovementRequest("شمارش اشتباه"))).StatusCode);
        var net = await InDb(db => (from t in db.LedgerTransactions join e in db.LedgerEntries on t.Id equals e.TransactionId
                                    where t.SourceType == "StockMovement" && t.SourceId == movement && e.AccountCode == Accounts.Inventory
                                    select e.Debit - e.Credit).SumAsync());
        Assert.Equal(0m, net);
        Assert.Equal(2, await InDb(db => db.LedgerTransactions.CountAsync(t => t.SourceType == "StockMovement" && t.SourceId == movement)));
    }

    [Fact]
    public async Task Opening_stock_typed_in_the_form_needs_no_supplier_and_owes_nothing()
    {
        var name = $"Os{Unique()}";
        var user = await ImportUser();
        var (priced, unpriced) = await InDb(async db =>
        {
            var a = new Product { Code = $"OS-{Unique()}", Name = $"{name} A", Kind = ProductKind.Drug, Unit = "دانه", SalePrice = 100 };
            var b = new Product { Code = $"OS-{Unique()}", Name = $"{name} B", Kind = ProductKind.Drug, Unit = "دانه", SalePrice = 100 };
            db.Products.AddRange(a, b);
            await db.SaveChangesAsync();
            return (a.Id, b.Id);
        });

        var inventory = await api.LoginAsync(user);
        var purchasesBefore = await InDb(db => db.Purchases.CountAsync());
        var request = new OpeningStockRequest("central", [new PurchaseLineInput(priced, "O1", new DateOnly(2028, 5, 1), 4, 25), new PurchaseLineInput(unpriced, "", null, 7, 0)], "شمارش قوس");
        Assert.Equal(HttpStatusCode.Created, (await inventory.SendAsync(ApiFixture.Keyed("/api/inventory/opening-stock", request))).StatusCode);

        Assert.Equal(purchasesBefore, await InDb(db => db.Purchases.CountAsync()));
        var movements = await InDb(db => db.StockMovements.Where(m => m.Batch!.Product!.Name.StartsWith(name))
            .Select(m => new { m.Id, m.Kind, m.Quantity, m.SourceType, m.Batch!.ProductId }).ToListAsync());
        Assert.Equal(2, movements.Count);
        Assert.All(movements, m => Assert.Equal((MovementKind.Adjustment, OpeningStockImport.SourceType), (m.Kind, m.SourceType)));
        Assert.Equal(4m, movements.Single(m => m.ProductId == priced).Quantity);
        Assert.Equal(7m, movements.Single(m => m.ProductId == unpriced).Quantity);

        var pricedMovement = movements.Single(m => m.ProductId == priced).Id;
        var ids = movements.Select(m => m.Id).ToList();
        var entries = await InDb(db => (from t in db.LedgerTransactions join e in db.LedgerEntries on t.Id equals e.TransactionId
                                        where t.SourceType == "StockMovement" && ids.Contains(t.SourceId)
                                        select new { t.Kind, t.SourceId, e.AccountCode, e.Debit, e.Credit }).ToListAsync());
        Assert.All(entries, e => Assert.Equal((LedgerKind, pricedMovement), (e.Kind, e.SourceId)));
        Assert.Equal(100m, entries.Single(e => e.AccountCode == Accounts.Inventory).Debit);
        Assert.Equal(100m, entries.Single(e => e.AccountCode == Accounts.OpeningBalance).Credit);
        Assert.DoesNotContain(entries, e => e.AccountCode == Accounts.Payable);

        Assert.Equal(HttpStatusCode.NoContent, (await inventory.PostAsJsonAsync($"/api/inventory/movements/{pricedMovement}/reverse", new ReverseMovementRequest("شمارش اشتباه"))).StatusCode);
        var net = await InDb(db => (from t in db.LedgerTransactions join e in db.LedgerEntries on t.Id equals e.TransactionId
                                    where t.SourceType == "StockMovement" && t.SourceId == pricedMovement && e.AccountCode == Accounts.Inventory
                                    select e.Debit - e.Credit).SumAsync());
        Assert.Equal(0m, net);

        Assert.Equal(HttpStatusCode.BadRequest, (await inventory.SendAsync(ApiFixture.Keyed("/api/inventory/opening-stock", request with { LocationCode = "nowhere" }))).StatusCode);
    }

    [Fact]
    public void An_excel_sheet_reads_text_numbers_and_date_cells()
    {
        var path = Path.Combine(Path.GetTempPath(), $"stock-{Unique()}.xlsx");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            void Entry(string name, string xml)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                w.Write(xml);
            }

            const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            Entry("xl/workbook.xml", $"<workbook xmlns=\"{ns}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"s\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            Entry("xl/_rels/workbook.xml.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"x\" Target=\"worksheets/sheet1.xml\"/></Relationships>");
            Entry("xl/styles.xml", $"<styleSheet xmlns=\"{ns}\"><cellXfs><xf numFmtId=\"0\"/><xf numFmtId=\"14\"/></cellXfs></styleSheet>");
            var headers = Header.Split(',');
            string Cell(string r, string text) => $"<c r=\"{r}\" t=\"inlineStr\"><is><t>{text}</t></is></c>";
            var head = string.Concat(headers.Select((h, i) => Cell($"{(char)('A' + i)}1", h)));
            var row = Cell("A2", "Atropine") + Cell("B2", "1%") + Cell("C2", "دانه") + "<c r=\"D2\"><v>7</v></c><c r=\"E2\"><v>12.5</v></c>"
                + Cell("G2", "AT9") + "<c r=\"H2\" s=\"1\"><v>46356</v></c>" + Cell("I2", "دواخانه") + Cell("J2", "دوا");
            Entry("xl/worksheets/sheet1.xml", $"<worksheet xmlns=\"{ns}\"><sheetData><row r=\"1\">{head}</row><row r=\"2\">{row}</row></sheetData></worksheet>");
        }

        var r = Read(path).Single();
        Assert.Equal(("Atropine", "7", "12.5", "", "AT9", "2026-11-30", "دواخانه"), (r.Name, r.Quantity, r.PurchasePrice, r.SalePrice, r.Batch, r.Expiry, r.Location));
    }
}
