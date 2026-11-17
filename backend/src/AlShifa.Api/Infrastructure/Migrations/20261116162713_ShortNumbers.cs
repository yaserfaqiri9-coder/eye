using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlShifa.Api.Infrastructure.Migrations
{
    /// <summary>
    /// File, document and product numbers become plain digits, as people read them: P-000123 becomes 123, INV-0000045 becomes 45.
    /// The digits are the same sequence the system already counted, so each stays unique and new numbers simply continue.
    /// Products whose code is not one to four digits (older opening-stock imports, OS-...) get the next free four-digit codes.
    /// Down does nothing: the old form cannot be rebuilt from the plain number with certainty, and the plain form works with the old code too.
    /// </summary>
    public partial class ShortNumbers : Migration
    {
        private static readonly (string Table, string Column)[] Numbers =
        [
            ("Patients", "FileNumber"),
            ("Visits", "VisitNumber"),
            ("Invoices", "InvoiceNumber"),
            ("Payments", "ReceiptNumber"),
            ("Refunds", "RefundNumber"),
            ("Expenses", "ExpenseNumber"),
            ("Purchases", "PurchaseNumber"),
            ("Sales", "SaleNumber"),
            ("OpticalOrders", "OrderNumber"),
            ("DiagnosticOrders", "OrderNumber"),
            ("MedicalPrescriptions", "RxNumber"),
            ("OpticalPrescriptions", "RxNumber"),
            ("SurgeryCases", "CaseNumber"),
        ];

        private const string ProductSequence = """
            INSERT INTO NumberSequences (Scope, Period, Value)
            SELECT 'code:product', '', COALESCE(MAX(CAST(Code AS UNSIGNED)), 0) FROM Products WHERE Code REGEXP '^[0-9]{1,4}$'
            ON DUPLICATE KEY UPDATE Value = GREATEST(Value, VALUES(Value));
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var (table, column) in Numbers)
            {
                migrationBuilder.Sql($"""
                    UPDATE `{table}` SET `{column}` = CAST(CAST(SUBSTRING_INDEX(`{column}`, '-', -1) AS UNSIGNED) AS CHAR)
                    WHERE `{column}` REGEXP '^[A-Za-z]+-[0-9]+$';
                    """);
            }

            // Numbered after the highest code in use or ever issued, so no new code meets an old one.
            migrationBuilder.Sql(ProductSequence);
            migrationBuilder.Sql("""
                UPDATE Products p
                JOIN (SELECT Id, ROW_NUMBER() OVER (ORDER BY Id) AS n FROM Products WHERE Code NOT REGEXP '^[0-9]{1,4}$') r ON r.Id = p.Id
                JOIN NumberSequences s ON s.Scope = 'code:product' AND s.Period = ''
                SET p.Code = CAST(s.Value + r.n AS CHAR);
                """);
            migrationBuilder.Sql(ProductSequence);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
