using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Inventory;
using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static AlShifa.Api.Features.Inventory.OpeningStockImport;

namespace AlShifa.Tests;

/// <summary>The opening stock sheet: only clear rows become stock, once, and nothing else in the database changes.</summary>
[Collection("api")]
public sealed class OpeningStockImportTests(ApiFixture api)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private static string Unique() => Guid.NewGuid().ToString("N")[..8];

    private static StagingRow Row(string id, string name, string? strength = "1%", decimal? quantity = 19, string unit = "دانه", int? month = 11, int? year = 2028,
        string group = "medicine", string? issues = "", string action = "stage_only") =>
        new(id, group, "قطره چشمی", name, strength, strength is null ? null : "medicine_strength", $"{quantity} {unit}", quantity, unit, null,
            month is null ? null : $"{month}/{year}", month, year, null, issues, action);

    private static string Write(string dataset, params StagingRow[] rows)
    {
        var path = Path.Combine(Path.GetTempPath(), $"opening-{dataset}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new StagingFile(dataset, rows.Length, rows.ToList()), Json));
        return path;
    }

    private async Task<Result> Run(string path, string user, bool apply)
    {
        using var scope = api.Services.CreateScope();
        return await RunAsync(scope.ServiceProvider, path, user, apply, backupTaken: true, TextWriter.Null);
    }

    private async Task<T> InDb<T>(Func<AppDbContext, Task<T>> read)
    {
        using var scope = api.Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    [Fact]
    public void Only_a_row_with_name_strength_quantity_and_month_is_imported_and_expires_at_month_end()
    {
        var today = new DateOnly(2026, 11, 11);
        var clear = Evaluate(Row("P1", "Glaupin", month: 2, year: 2028), today);
        Assert.Equal(Outcome.Import, clear.Outcome);
        Assert.Equal(new DateOnly(2028, 2, 29), clear.Expiry);

        Assert.Equal(Outcome.Excluded, Evaluate(Row("P2", "X", action: "exclude"), today).Outcome);
        Assert.Equal(Outcome.Held, Evaluate(Row("P3", "X", strength: null, issues: "دوز/غلظت ذکر نشده"), today).Outcome);
        Assert.Equal(Outcome.Held, Evaluate(Row("P4", "X", quantity: null), today).Outcome);
        Assert.Equal(Outcome.Held, Evaluate(Row("P5", "X", month: null), today).Outcome);
        Assert.Equal(Outcome.Held, Evaluate(Row("P6", "X", issues: "نام نیاز به بسته دارد"), today).Outcome);
        Assert.Equal(Outcome.Held, Evaluate(Row("P7", "X", group: "surgical_supply"), today).Outcome);
        Assert.Equal(Outcome.Held, Evaluate(Row("P8", "X", unit: "کارتن"), today).Outcome);
        Assert.Equal(Outcome.Held, Evaluate(Row("P9", "X", month: 10, year: 2026), today).Outcome);
    }

    [Fact]
    public async Task Clear_rows_become_inactive_stock_once_without_ledger_and_a_rerun_adds_nothing()
    {
        var dataset = Unique();
        var name = $"Drop{Unique()}";
        var user = $"imp{Unique()}";
        await api.CreateUserAsync(user, Roles.Inventory);
        var path = Write(dataset,
            Row("P01-1", name, "1%", 19, month: 11, year: 2028),
            Row("P01-2", name, "4%", 9, month: 7, year: 2027),
            Row("P01-3", $"{name}x", strength: null, issues: "دوز/غلظت ذکر نشده"),
            Row("P01-X", $"{name}y", action: "exclude"));

        var ledgerBefore = await InDb(db => db.LedgerTransactions.CountAsync());
        var dry = await Run(path, user, apply: false);
        Assert.Equal(2, dry.Count(Outcome.Import));
        Assert.False(await InDb(db => db.Products.AnyAsync(p => p.Name.StartsWith(name))));

        var first = await Run(path, user, apply: true);
        Assert.Equal((2, 1, 1), (first.Count(Outcome.Import), first.Count(Outcome.Held), first.Count(Outcome.Excluded)));

        var second = await Run(path, user, apply: true);
        Assert.Equal((0, 2), (second.Count(Outcome.Import), second.Count(Outcome.AlreadyImported)));

        var stock = await InDb(db => db.StockBatches.AsNoTracking().Where(b => b.Product!.Name.StartsWith(name))
            .Select(b => new { b.Product!.Name, b.Product.Unit, b.Product.IsActive, b.Product.SalePrice, b.Product.Category, b.Quantity, b.ExpiryDate, b.UnitCost, b.LotNumber, Location = b.Location!.Code })
            .OrderBy(b => b.Name).ToListAsync());
        Assert.Equal(2, stock.Count);
        Assert.Equal(($"{name} 1%", "دانه", 19m, new DateOnly(2028, 11, 30)), (stock[0].Name, stock[0].Unit, stock[0].Quantity, stock[0].ExpiryDate));
        Assert.Equal(($"{name} 4%", 9m, new DateOnly(2027, 7, 31)), (stock[1].Name, stock[1].Quantity, stock[1].ExpiryDate));
        Assert.All(stock, s => Assert.Equal((false, 0m, 0m, "", "pharmacy", "قطره چشمی"), (s.IsActive, s.SalePrice, s.UnitCost, s.LotNumber, s.Location, s.Category)));
        Assert.Equal(2, await InDb(db => db.StockMovements.CountAsync(m => m.SourceType == SourceType && m.Reason!.StartsWith($"[opening-{dataset}/"))));
        Assert.Equal(ledgerBefore, await InDb(db => db.LedgerTransactions.CountAsync()));
    }

    [Fact]
    public async Task A_same_named_product_already_in_the_system_stops_the_row_instead_of_merging()
    {
        var name = $"Tobra{Unique()}";
        var user = $"imp{Unique()}";
        await api.CreateUserAsync(user, Roles.Inventory);
        await InDb(async db =>
        {
            db.Products.Add(new Product { Code = $"EX-{Unique()}", Name = $"{name} 0.3% eye drops", Kind = ProductKind.Drug, Unit = "دانه", SalePrice = 100 });
            return await db.SaveChangesAsync();
        });

        var result = await Run(Write(Unique(), Row("P1", name, "0.3%")), user, apply: true);
        Assert.Equal(Outcome.Held, result.Decisions.Single().Outcome);
        Assert.Equal(1, await InDb(db => db.Products.CountAsync(p => p.Name.StartsWith(name))));
    }

    [Fact]
    public async Task A_mistaken_opening_row_can_be_reversed_from_the_movements_page()
    {
        var name = $"Rev{Unique()}";
        var user = $"imp{Unique()}";
        await api.CreateUserAsync(user, Roles.Inventory);
        var dataset = Unique();
        await Run(Write(dataset, Row("P1", name, "2%", 5)), user, apply: true);
        var movement = await InDb(db => db.StockMovements.Where(m => m.SourceType == SourceType && m.Reason!.StartsWith($"[opening-{dataset}/")).Select(m => m.Id).SingleAsync());

        var inventory = await api.LoginAsync(user);
        Assert.Equal(HttpStatusCode.NoContent, (await inventory.PostAsJsonAsync($"/api/inventory/movements/{movement}/reverse", new ReverseMovementRequest("شمارش اشتباه"))).StatusCode);
        Assert.Equal(0m, await InDb(db => db.StockBatches.Where(b => b.Product!.Name.StartsWith(name)).SumAsync(b => b.Quantity)));

        // The reversed row stays recorded, so running the sheet again does not bring it back.
        var again = await Run(Write(dataset, Row("P1", name, "2%", 5)), user, apply: true);
        Assert.Equal(Outcome.AlreadyImported, again.Decisions.Single().Outcome);
    }
}
