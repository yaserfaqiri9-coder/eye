using System;
using Microsoft.EntityFrameworkCore.Migrations;
using MySql.EntityFrameworkCore.Metadata;

#nullable disable

namespace AlShifa.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PatientCalls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PatientCalls",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    VisitId = table.Column<long>(type: "bigint", nullable: true),
                    VisitTokenId = table.Column<long>(type: "bigint", nullable: true),
                    Desk = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false),
                    QueueNumber = table.Column<int>(type: "int", nullable: false),
                    Destination = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false),
                    IsRecall = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Status = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false),
                    Outcome = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: true),
                    FailureReason = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true),
                    RequestedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ClaimedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    OutcomeByUserId = table.Column<long>(type: "bigint", nullable: true),
                    OutcomeAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PatientCalls", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PatientCalls_VisitTokens_VisitTokenId",
                        column: x => x.VisitTokenId,
                        principalTable: "VisitTokens",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PatientCalls_Visits_VisitId",
                        column: x => x.VisitId,
                        principalTable: "Visits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_PatientCalls_RequestedAt",
                table: "PatientCalls",
                column: "RequestedAt");

            migrationBuilder.CreateIndex(
                name: "IX_PatientCalls_Status_Id",
                table: "PatientCalls",
                columns: new[] { "Status", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_PatientCalls_VisitId_Desk_Id",
                table: "PatientCalls",
                columns: new[] { "VisitId", "Desk", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_PatientCalls_VisitTokenId_Desk_Id",
                table: "PatientCalls",
                columns: new[] { "VisitTokenId", "Desk", "Id" });

            // Queue numbers move to one sequence per day. Continuing after the highest number each department already
            // issued keeps an update on a working day from colliding with visits registered that morning.
            migrationBuilder.Sql("INSERT INTO NumberSequences (Scope, Period, Value) SELECT 'queue', Period, MAX(Value) FROM NumberSequences WHERE Scope LIKE 'queue:%' GROUP BY Period;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PatientCalls");
        }
    }
}
