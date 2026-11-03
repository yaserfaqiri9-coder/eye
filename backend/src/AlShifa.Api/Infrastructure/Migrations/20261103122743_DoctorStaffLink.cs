using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlShifa.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DoctorStaffLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The new link goes on first. If it fails (a visit naming a staff row that does not exist), nothing has been dropped yet.
            migrationBuilder.Sql("UPDATE `Visits` SET `DoctorStaffId` = `DoctorId` WHERE `DoctorStaffId` IS NULL AND `DoctorId` IS NOT NULL;");
            migrationBuilder.Sql("UPDATE `Appointments` SET `DoctorStaffId` = `DoctorId` WHERE `DoctorStaffId` = 0 AND `DoctorId` IS NOT NULL;");

            migrationBuilder.CreateIndex(
                name: "IX_Visits_DoctorStaffId",
                table: "Visits",
                column: "DoctorStaffId");

            migrationBuilder.AddForeignKey(
                name: "FK_Appointments_Staff_DoctorStaffId",
                table: "Appointments",
                column: "DoctorStaffId",
                principalTable: "Staff",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Visits_Staff_DoctorStaffId",
                table: "Visits",
                column: "DoctorStaffId",
                principalTable: "Staff",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // Nothing in the application ever wrote these two columns.
            migrationBuilder.DropForeignKey(
                name: "FK_Appointments_Staff_DoctorId",
                table: "Appointments");

            migrationBuilder.DropForeignKey(
                name: "FK_Visits_Staff_DoctorId",
                table: "Visits");

            migrationBuilder.DropIndex(
                name: "IX_Visits_DoctorId",
                table: "Visits");

            migrationBuilder.DropIndex(
                name: "IX_Appointments_DoctorId",
                table: "Appointments");

            migrationBuilder.DropColumn(
                name: "DoctorId",
                table: "Visits");

            migrationBuilder.DropColumn(
                name: "DoctorId",
                table: "Appointments");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Appointments_Staff_DoctorStaffId",
                table: "Appointments");

            migrationBuilder.DropForeignKey(
                name: "FK_Visits_Staff_DoctorStaffId",
                table: "Visits");

            migrationBuilder.DropIndex(
                name: "IX_Visits_DoctorStaffId",
                table: "Visits");

            migrationBuilder.AddColumn<long>(
                name: "DoctorId",
                table: "Visits",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DoctorId",
                table: "Appointments",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Visits_DoctorId",
                table: "Visits",
                column: "DoctorId");

            migrationBuilder.CreateIndex(
                name: "IX_Appointments_DoctorId",
                table: "Appointments",
                column: "DoctorId");

            migrationBuilder.AddForeignKey(
                name: "FK_Appointments_Staff_DoctorId",
                table: "Appointments",
                column: "DoctorId",
                principalTable: "Staff",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Visits_Staff_DoctorId",
                table: "Visits",
                column: "DoctorId",
                principalTable: "Staff",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
