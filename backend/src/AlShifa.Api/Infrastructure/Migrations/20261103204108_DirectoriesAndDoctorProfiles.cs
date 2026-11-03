using Microsoft.EntityFrameworkCore.Migrations;
using MySql.EntityFrameworkCore.Metadata;

#nullable disable

namespace AlShifa.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DirectoriesAndDoctorProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Avatar",
                table: "Staff",
                type: "int",
                nullable: false,
                defaultValue: 12);

            migrationBuilder.AddColumn<string>(
                name: "Biography",
                table: "Staff",
                type: "varchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Facebook",
                table: "Staff",
                type: "varchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Google",
                table: "Staff",
                type: "varchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Specialty",
                table: "Staff",
                type: "varchar(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Twitter",
                table: "Staff",
                type: "varchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Website",
                table: "Staff",
                type: "varchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SupplierId",
                table: "Purchases",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "Departments",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "Suppliers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    Name = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: false),
                    Phone = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: true),
                    Address = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true),
                    IsActive = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Suppliers", x => x.Id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_Purchases_SupplierId",
                table: "Purchases",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_Suppliers_Name",
                table: "Suppliers",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Purchases_Suppliers_SupplierId",
                table: "Purchases",
                column: "SupplierId",
                principalTable: "Suppliers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Purchases_Suppliers_SupplierId",
                table: "Purchases");

            migrationBuilder.DropTable(
                name: "Suppliers");

            migrationBuilder.DropIndex(
                name: "IX_Purchases_SupplierId",
                table: "Purchases");

            migrationBuilder.DropColumn(
                name: "Avatar",
                table: "Staff");

            migrationBuilder.DropColumn(
                name: "Biography",
                table: "Staff");

            migrationBuilder.DropColumn(
                name: "Facebook",
                table: "Staff");

            migrationBuilder.DropColumn(
                name: "Google",
                table: "Staff");

            migrationBuilder.DropColumn(
                name: "Specialty",
                table: "Staff");

            migrationBuilder.DropColumn(
                name: "Twitter",
                table: "Staff");

            migrationBuilder.DropColumn(
                name: "Website",
                table: "Staff");

            migrationBuilder.DropColumn(
                name: "SupplierId",
                table: "Purchases");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "Departments");
        }
    }
}
