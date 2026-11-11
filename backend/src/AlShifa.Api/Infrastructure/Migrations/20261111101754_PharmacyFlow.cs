using System;
using Microsoft.EntityFrameworkCore.Migrations;
using MySql.EntityFrameworkCore.Metadata;

#nullable disable

namespace AlShifa.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PharmacyFlow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DeliveredAt",
                table: "Sales",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DeliveredByUserId",
                table: "Sales",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UnpaidDeliveryReason",
                table: "Sales",
                type: "varchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PharmacyStatus",
                table: "MedicalPrescriptions",
                type: "varchar(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "AwaitingVisit");

            migrationBuilder.AddColumn<DateTime>(
                name: "ValidUntil",
                table: "MedicalPrescriptions",
                type: "date",
                nullable: true);

            // Before this version a sale took its stock when it was made, so every existing sale was delivered then.
            migrationBuilder.Sql("UPDATE Sales SET DeliveredAt = CreatedAt, DeliveredByUserId = CreatedByUserId WHERE DeliveredAt IS NULL;");

            // A prescription the pharmacy already worked on was visited; the rest still await the patient.
            migrationBuilder.Sql("""
                UPDATE MedicalPrescriptions p
                SET p.PharmacyStatus = 'AwaitingDecision'
                WHERE p.Status IN ('PartiallyDispensed', 'Dispensed') OR EXISTS (SELECT 1 FROM Sales s WHERE s.PrescriptionId = p.Id);
                """);

            migrationBuilder.CreateTable(
                name: "PrescriptionPharmacyEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    PrescriptionId = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false),
                    Note = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrescriptionPharmacyEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PrescriptionPharmacyEvents_MedicalPrescriptions_Prescription~",
                        column: x => x.PrescriptionId,
                        principalTable: "MedicalPrescriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_Sales_Kind_DeliveredAt",
                table: "Sales",
                columns: new[] { "Kind", "DeliveredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MedicalPrescriptions_PatientId_Id",
                table: "MedicalPrescriptions",
                columns: new[] { "PatientId", "Id" });

            migrationBuilder.DropIndex(
                name: "IX_MedicalPrescriptions_PatientId",
                table: "MedicalPrescriptions");

            migrationBuilder.CreateIndex(
                name: "IX_PrescriptionPharmacyEvents_PrescriptionId_Id",
                table: "PrescriptionPharmacyEvents",
                columns: new[] { "PrescriptionId", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PrescriptionPharmacyEvents");

            migrationBuilder.DropIndex(
                name: "IX_Sales_Kind_DeliveredAt",
                table: "Sales");

            migrationBuilder.CreateIndex(
                name: "IX_MedicalPrescriptions_PatientId",
                table: "MedicalPrescriptions",
                column: "PatientId");

            migrationBuilder.DropIndex(
                name: "IX_MedicalPrescriptions_PatientId_Id",
                table: "MedicalPrescriptions");

            migrationBuilder.DropColumn(
                name: "DeliveredAt",
                table: "Sales");

            migrationBuilder.DropColumn(
                name: "DeliveredByUserId",
                table: "Sales");

            migrationBuilder.DropColumn(
                name: "UnpaidDeliveryReason",
                table: "Sales");

            migrationBuilder.DropColumn(
                name: "PharmacyStatus",
                table: "MedicalPrescriptions");

            migrationBuilder.DropColumn(
                name: "ValidUntil",
                table: "MedicalPrescriptions");

        }
    }
}
