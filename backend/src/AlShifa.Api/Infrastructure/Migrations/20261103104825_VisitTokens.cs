using System;
using Microsoft.EntityFrameworkCore.Migrations;
using MySql.EntityFrameworkCore.Metadata;

#nullable disable

namespace AlShifa.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class VisitTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "DiagnosticTests",
                type: "varchar(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "Lab");

            migrationBuilder.AddColumn<long>(
                name: "InvoiceId",
                table: "DiagnosticOrders",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ReturnToDoctor",
                table: "DiagnosticOrders",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "VisitTokens",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    QueueNumber = table.Column<int>(type: "int", nullable: false),
                    TokenDate = table.Column<DateTime>(type: "date", nullable: false),
                    DepartmentId = table.Column<int>(type: "int", nullable: false),
                    DoctorStaffId = table.Column<long>(type: "bigint", nullable: true),
                    InvoiceId = table.Column<long>(type: "bigint", nullable: false),
                    VisitId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VisitTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VisitTokens_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VisitTokens_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VisitTokens_Staff_DoctorStaffId",
                        column: x => x.DoctorStaffId,
                        principalTable: "Staff",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VisitTokens_Visits_VisitId",
                        column: x => x.VisitId,
                        principalTable: "Visits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_DiagnosticOrders_InvoiceId",
                table: "DiagnosticOrders",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_VisitTokens_DepartmentId",
                table: "VisitTokens",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_VisitTokens_DoctorStaffId",
                table: "VisitTokens",
                column: "DoctorStaffId");

            migrationBuilder.CreateIndex(
                name: "IX_VisitTokens_InvoiceId",
                table: "VisitTokens",
                column: "InvoiceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VisitTokens_TokenDate",
                table: "VisitTokens",
                column: "TokenDate");

            migrationBuilder.CreateIndex(
                name: "IX_VisitTokens_VisitId",
                table: "VisitTokens",
                column: "VisitId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_DiagnosticOrders_Invoices_InvoiceId",
                table: "DiagnosticOrders",
                column: "InvoiceId",
                principalTable: "Invoices",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_DiagnosticOrders_Invoices_InvoiceId",
                table: "DiagnosticOrders");

            migrationBuilder.DropTable(
                name: "VisitTokens");

            migrationBuilder.DropIndex(
                name: "IX_DiagnosticOrders_InvoiceId",
                table: "DiagnosticOrders");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "DiagnosticTests");

            migrationBuilder.DropColumn(
                name: "InvoiceId",
                table: "DiagnosticOrders");

            migrationBuilder.DropColumn(
                name: "ReturnToDoctor",
                table: "DiagnosticOrders");
        }
    }
}
