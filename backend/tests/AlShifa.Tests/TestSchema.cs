using System.Security.Cryptography;
using System.Text;
using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AlShifa.Tests;

/// <summary>
/// Keeps the test schema between runs. Building it from the migrations takes about 15 seconds on every run,
/// so when the model and migration list are unchanged the tables are only emptied, which leaves the database as a fresh one would be.
/// Set ALSHIFA_TEST_FRESH_DB=1 to force a full rebuild, for example after editing a migration in place.
/// </summary>
internal static class TestSchema
{
    private const string KeyTable = "__TestSchemaKey";

    public static async Task ResetAsync(AppDbContext db)
    {
        if (Environment.GetEnvironmentVariable("ALSHIFA_TEST_FRESH_DB") != "1" && await IsCurrentAsync(db))
        {
            await EmptyAsync(db);
        }
        else
        {
            await db.Database.EnsureDeletedAsync();
        }
    }

    /// <summary>Records which model built the schema, once the API has migrated and seeded it.</summary>
    public static async Task MarkCurrentAsync(AppDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync($"CREATE TABLE IF NOT EXISTS `{KeyTable}` (`Key` varchar(64) NOT NULL)");
        await db.Database.ExecuteSqlRawAsync($"DELETE FROM `{KeyTable}`");
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO `__TestSchemaKey` (`Key`) VALUES ({Key(db)})");
    }

    private static async Task<bool> IsCurrentAsync(AppDbContext db)
    {
        if (!await db.Database.CanConnectAsync())
        {
            return false;
        }

        var hasKey = await db.Database
            .SqlQueryRaw<int>($"SELECT COUNT(*) AS `Value` FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = '{KeyTable}'")
            .SingleAsync() == 1;
        if (!hasKey)
        {
            return false;
        }

        var stored = await db.Database.SqlQueryRaw<string>($"SELECT `Key` AS `Value` FROM `{KeyTable}`").ToListAsync();
        return stored.Count == 1 && stored[0] == Key(db) && !(await db.Database.GetPendingMigrationsAsync()).Any();
    }

    /// <summary>The model and every migration id: an entity or migration change gives a different key.</summary>
    private static string Key(AppDbContext db)
    {
        var migrations = db.GetService<IMigrationsAssembly>().Migrations.Keys;
        var text = db.Model.ToDebugString(MetadataDebugStringOptions.LongDefault) + string.Join('|', migrations);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>
    /// Every table emptied; the seeder then fills reference data again. DELETE, not TRUNCATE: on MySQL TRUNCATE recreates each table
    /// (about 4 seconds for all of them). Auto-increment ids keep counting, which no test can rely on, since all tests share one database.
    /// </summary>
    private static async Task EmptyAsync(AppDbContext db)
    {
        var tables = await db.Database
            .SqlQueryRaw<string>("SELECT table_name AS `Value` FROM information_schema.tables WHERE table_schema = DATABASE() AND table_type = 'BASE TABLE' AND table_name NOT IN ('__EFMigrationsHistory', '__TestSchemaKey')")
            .ToListAsync();
        var sql = new StringBuilder("SET FOREIGN_KEY_CHECKS = 0;");
        foreach (var table in tables)
        {
            sql.Append($"DELETE FROM `{table}`;");
        }

        sql.Append("SET FOREIGN_KEY_CHECKS = 1;");
        await db.Database.OpenConnectionAsync();
        try
        {
            await db.Database.ExecuteSqlRawAsync(sql.ToString());
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}
