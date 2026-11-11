using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlShifa.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RefractionToOptical : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "RefractionId",
                table: "OpticalPrescriptions",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_OpticalPrescriptions_RefractionId",
                table: "OpticalPrescriptions",
                column: "RefractionId");

            migrationBuilder.AddForeignKey(
                name: "FK_OpticalPrescriptions_Refractions_RefractionId",
                table: "OpticalPrescriptions",
                column: "RefractionId",
                principalTable: "Refractions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_OpticalPrescriptions_Refractions_RefractionId",
                table: "OpticalPrescriptions");

            migrationBuilder.DropIndex(
                name: "IX_OpticalPrescriptions_RefractionId",
                table: "OpticalPrescriptions");

            migrationBuilder.DropColumn(
                name: "RefractionId",
                table: "OpticalPrescriptions");
        }
    }
}
