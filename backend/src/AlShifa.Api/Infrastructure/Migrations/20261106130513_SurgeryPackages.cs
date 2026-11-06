using System;
using Microsoft.EntityFrameworkCore.Migrations;
using MySql.EntityFrameworkCore.Metadata;

#nullable disable

namespace AlShifa.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SurgeryPackages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PackageId",
                table: "SurgeryCases",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SuppliesLocationId",
                table: "SurgeryCases",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SuppliesRecordedAt",
                table: "SurgeryCases",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SuppliesRecordedByUserId",
                table: "SurgeryCases",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SurgeryPackages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    Name = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false),
                    ServiceId = table.Column<int>(type: "int", nullable: false),
                    LocationId = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SurgeryPackages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SurgeryPackages_Services_ServiceId",
                        column: x => x.ServiceId,
                        principalTable: "Services",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SurgeryPackages_StockLocations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "StockLocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "SurgeryPackageItems",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    PackageId = table.Column<int>(type: "int", nullable: false),
                    ProductId = table.Column<int>(type: "int", nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SurgeryPackageItems", x => x.Id);
                    table.CheckConstraint("CK_SurgeryPackageItem_Quantity", "`Quantity` > 0");
                    table.ForeignKey(
                        name: "FK_SurgeryPackageItems_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SurgeryPackageItems_SurgeryPackages_PackageId",
                        column: x => x.PackageId,
                        principalTable: "SurgeryPackages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_SurgeryCases_PackageId",
                table: "SurgeryCases",
                column: "PackageId");

            migrationBuilder.CreateIndex(
                name: "IX_SurgeryCases_SuppliesLocationId",
                table: "SurgeryCases",
                column: "SuppliesLocationId");

            migrationBuilder.CreateIndex(
                name: "IX_SurgeryPackageItems_PackageId",
                table: "SurgeryPackageItems",
                column: "PackageId");

            migrationBuilder.CreateIndex(
                name: "IX_SurgeryPackageItems_ProductId",
                table: "SurgeryPackageItems",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_SurgeryPackages_LocationId",
                table: "SurgeryPackages",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_SurgeryPackages_Name",
                table: "SurgeryPackages",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SurgeryPackages_ServiceId",
                table: "SurgeryPackages",
                column: "ServiceId");

            migrationBuilder.AddForeignKey(
                name: "FK_SurgeryCases_StockLocations_SuppliesLocationId",
                table: "SurgeryCases",
                column: "SuppliesLocationId",
                principalTable: "StockLocations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SurgeryCases_SurgeryPackages_PackageId",
                table: "SurgeryCases",
                column: "PackageId",
                principalTable: "SurgeryPackages",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SurgeryCases_StockLocations_SuppliesLocationId",
                table: "SurgeryCases");

            migrationBuilder.DropForeignKey(
                name: "FK_SurgeryCases_SurgeryPackages_PackageId",
                table: "SurgeryCases");

            migrationBuilder.DropTable(
                name: "SurgeryPackageItems");

            migrationBuilder.DropTable(
                name: "SurgeryPackages");

            migrationBuilder.DropIndex(
                name: "IX_SurgeryCases_PackageId",
                table: "SurgeryCases");

            migrationBuilder.DropIndex(
                name: "IX_SurgeryCases_SuppliesLocationId",
                table: "SurgeryCases");

            migrationBuilder.DropColumn(
                name: "PackageId",
                table: "SurgeryCases");

            migrationBuilder.DropColumn(
                name: "SuppliesLocationId",
                table: "SurgeryCases");

            migrationBuilder.DropColumn(
                name: "SuppliesRecordedAt",
                table: "SurgeryCases");

            migrationBuilder.DropColumn(
                name: "SuppliesRecordedByUserId",
                table: "SurgeryCases");
        }
    }
}
