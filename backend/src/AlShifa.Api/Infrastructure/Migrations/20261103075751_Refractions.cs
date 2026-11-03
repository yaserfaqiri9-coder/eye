using System;
using Microsoft.EntityFrameworkCore.Migrations;
using MySql.EntityFrameworkCore.Metadata;

#nullable disable

namespace AlShifa.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Refractions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Refractions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    VisitId = table.Column<long>(type: "bigint", nullable: false),
                    UnaidedVaOd = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: true),
                    UnaidedVaOs = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: true),
                    PinholeVaOd = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: true),
                    PinholeVaOs = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: true),
                    NearVaOd = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: true),
                    NearVaOs = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: true),
                    Pd = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: true),
                    Measurements = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true),
                    Notes = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: true),
                    RecordedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    RecordedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Refractions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Refractions_Visits_VisitId",
                        column: x => x.VisitId,
                        principalTable: "Visits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "RefractionLines",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    RefractionId = table.Column<long>(type: "bigint", nullable: false),
                    Stage = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false),
                    Eye = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false),
                    Sphere = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: true),
                    Cylinder = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: true),
                    Axis = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: true),
                    Add = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: true),
                    Va = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RefractionLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RefractionLines_Refractions_RefractionId",
                        column: x => x.RefractionId,
                        principalTable: "Refractions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_RefractionLines_RefractionId_Stage_Eye",
                table: "RefractionLines",
                columns: new[] { "RefractionId", "Stage", "Eye" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Refractions_VisitId",
                table: "Refractions",
                column: "VisitId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RefractionLines");

            migrationBuilder.DropTable(
                name: "Refractions");
        }
    }
}
