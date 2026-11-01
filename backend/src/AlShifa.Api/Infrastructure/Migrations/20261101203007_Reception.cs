using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlShifa.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Reception : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ActiveVisitKey",
                table: "Visits",
                type: "varchar(60)",
                maxLength: 60,
                nullable: true,
                computedColumnSql: "CASE WHEN `Status` NOT IN ('Completed','Cancelled') THEN CONCAT(`PatientId`,'|',`DepartmentId`,'|',`VisitDate`) END",
                stored: true);

            migrationBuilder.AddColumn<string>(
                name: "ActiveSlotKey",
                table: "Appointments",
                type: "varchar(60)",
                maxLength: 60,
                nullable: true,
                computedColumnSql: "CASE WHEN `Status` = 'Scheduled' THEN CONCAT(`DoctorStaffId`,'|',DATE_FORMAT(`ScheduledAt`,'%Y%m%d%H%i')) END",
                stored: true);

            migrationBuilder.CreateIndex(
                name: "IX_Visits_ActiveVisitKey",
                table: "Visits",
                column: "ActiveVisitKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_ActiveSlotKey",
                table: "Appointments",
                column: "ActiveSlotKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Visits_ActiveVisitKey",
                table: "Visits");

            migrationBuilder.DropIndex(
                name: "IX_Appointments_ActiveSlotKey",
                table: "Appointments");

            migrationBuilder.DropColumn(
                name: "ActiveVisitKey",
                table: "Visits");

            migrationBuilder.DropColumn(
                name: "ActiveSlotKey",
                table: "Appointments");
        }
    }
}
