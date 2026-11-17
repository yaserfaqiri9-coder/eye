using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlShifa.Api.Infrastructure.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261117140000_PharmacyProductCodes")]
public partial class PharmacyProductCodes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TEMPORARY TABLE IF EXISTS PharmacyProductCodeMap;");

        migrationBuilder.Sql("""
            CREATE TEMPORARY TABLE PharmacyProductCodeMap (
                ProductId INT NOT NULL PRIMARY KEY,
                NewCode INT NOT NULL
            );
            """);

        migrationBuilder.Sql("""
            INSERT INTO PharmacyProductCodeMap (ProductId, NewCode)
            SELECT p.Id, 9 + ROW_NUMBER() OVER (ORDER BY p.Id)
            FROM Products p
            WHERE EXISTS (
                SELECT 1
                FROM StockBatches b
                JOIN StockLocations l ON l.Id = b.LocationId
                WHERE b.ProductId = p.Id AND l.Code = 'pharmacy'
            );
            """);

        migrationBuilder.Sql("""
            UPDATE Products p
            JOIN PharmacyProductCodeMap m ON m.ProductId = p.Id
            SET p.Code = CONCAT('~P', p.Id, '-', LEFT(REPLACE(UUID(), '-', ''), 20));
            """);

        migrationBuilder.Sql("""
            UPDATE Products p
            JOIN PharmacyProductCodeMap m ON p.Code = CAST(m.NewCode AS CHAR)
            SET p.Code = CONCAT('~C', p.Id, '-', LEFT(REPLACE(UUID(), '-', ''), 20));
            """);

        migrationBuilder.Sql("""
            UPDATE Products p
            JOIN PharmacyProductCodeMap m ON m.ProductId = p.Id
            SET p.Code = CAST(m.NewCode AS CHAR);
            """);

        migrationBuilder.Sql("""
            UPDATE Products p
            JOIN (
                SELECT Id, GREATEST(base.Value, pharmacy.MaxCode) + ROW_NUMBER() OVER (ORDER BY Id) AS NewCode
                FROM Products
                CROSS JOIN (
                    SELECT COALESCE(Value, 9) AS Value
                    FROM NumberSequences
                    WHERE Scope = 'code:product' AND Period = ''
                ) base
                CROSS JOIN (
                    SELECT COALESCE(MAX(NewCode), 9) AS MaxCode
                    FROM PharmacyProductCodeMap
                ) pharmacy
                WHERE Code LIKE '~C%'
            ) displaced ON displaced.Id = p.Id
            SET p.Code = CAST(displaced.NewCode AS CHAR);
            """);

        migrationBuilder.Sql("""
            INSERT INTO NumberSequences (Scope, Period, Value)
            SELECT 'code:product', '', GREATEST(9, COALESCE(MAX(CAST(Code AS UNSIGNED)), 0))
            FROM Products
            WHERE Code REGEXP '^[0-9]+$'
            ON DUPLICATE KEY UPDATE Value = GREATEST(Value, VALUES(Value));
            """);

        migrationBuilder.Sql("DROP TEMPORARY TABLE PharmacyProductCodeMap;");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
    }
}