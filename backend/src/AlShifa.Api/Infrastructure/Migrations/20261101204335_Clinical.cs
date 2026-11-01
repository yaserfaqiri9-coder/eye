using System;
using Microsoft.EntityFrameworkCore.Migrations;
using MySql.EntityFrameworkCore.Metadata;

#nullable disable

namespace AlShifa.Api.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Clinical : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClinicalAccessGrants",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    PatientId = table.Column<long>(type: "bigint", nullable: false),
                    Reason = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClinicalAccessGrants", x => x.Id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ClinicalAmendments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    Entity = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false),
                    EntityId = table.Column<long>(type: "bigint", nullable: false),
                    Field = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false),
                    OldValue = table.Column<string>(type: "varchar(4000)", maxLength: 4000, nullable: true),
                    NewValue = table.Column<string>(type: "varchar(4000)", maxLength: 4000, nullable: true),
                    Reason = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClinicalAmendments", x => x.Id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Consultations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    VisitId = table.Column<long>(type: "bigint", nullable: false),
                    AuthorUserId = table.Column<long>(type: "bigint", nullable: false),
                    FindingsOd = table.Column<string>(type: "varchar(4000)", maxLength: 4000, nullable: true),
                    FindingsOs = table.Column<string>(type: "varchar(4000)", maxLength: 4000, nullable: true),
                    TreatmentPlan = table.Column<string>(type: "varchar(4000)", maxLength: 4000, nullable: true),
                    ConfidentialNote = table.Column<string>(type: "varchar(4000)", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    FinalizedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Consultations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Consultations_Visits_VisitId",
                        column: x => x.VisitId,
                        principalTable: "Visits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "DiagnosticTests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    Name = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: false),
                    IsActive = table.Column<bool>(type: "tinyint(1)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiagnosticTests", x => x.Id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "FollowUps",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    PatientId = table.Column<long>(type: "bigint", nullable: false),
                    VisitId = table.Column<long>(type: "bigint", nullable: false),
                    DueDate = table.Column<DateTime>(type: "date", nullable: false),
                    Note = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true),
                    CreatedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FollowUps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FollowUps_Patients_PatientId",
                        column: x => x.PatientId,
                        principalTable: "Patients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "MedicalPrescriptions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    RxNumber = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    PatientId = table.Column<long>(type: "bigint", nullable: false),
                    VisitId = table.Column<long>(type: "bigint", nullable: false),
                    DoctorUserId = table.Column<long>(type: "bigint", nullable: false),
                    Notes = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MedicalPrescriptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MedicalPrescriptions_Patients_PatientId",
                        column: x => x.PatientId,
                        principalTable: "Patients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MedicalPrescriptions_Visits_VisitId",
                        column: x => x.VisitId,
                        principalTable: "Visits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "OpticalPrescriptions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    RxNumber = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    PatientId = table.Column<long>(type: "bigint", nullable: false),
                    VisitId = table.Column<long>(type: "bigint", nullable: false),
                    DoctorUserId = table.Column<long>(type: "bigint", nullable: false),
                    SphereOd = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: true),
                    CylinderOd = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: true),
                    AxisOd = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: true),
                    AddOd = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: true),
                    SphereOs = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: true),
                    CylinderOs = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: true),
                    AxisOs = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: true),
                    AddOs = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: true),
                    Pd = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: true),
                    Notes = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true),
                    IsCancelled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpticalPrescriptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OpticalPrescriptions_Patients_PatientId",
                        column: x => x.PatientId,
                        principalTable: "Patients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OpticalPrescriptions_Visits_VisitId",
                        column: x => x.VisitId,
                        principalTable: "Visits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PreExaminations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    VisitId = table.Column<long>(type: "bigint", nullable: false),
                    ChiefComplaint = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),
                    History = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true),
                    VisualAcuityOd = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: true),
                    VisualAcuityOs = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: true),
                    IopOd = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: true),
                    IopOs = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: true),
                    Measurements = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true),
                    Notes = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: true),
                    RecordedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    RecordedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    Version = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PreExaminations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PreExaminations_Visits_VisitId",
                        column: x => x.VisitId,
                        principalTable: "Visits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "SurgeryCases",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    CaseNumber = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    PatientId = table.Column<long>(type: "bigint", nullable: false),
                    VisitId = table.Column<long>(type: "bigint", nullable: false),
                    SurgeryType = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false),
                    Eye = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false),
                    ResponsibleUserId = table.Column<long>(type: "bigint", nullable: false),
                    ScheduledAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    Status = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false),
                    PreOpChecks = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: true),
                    ConsentRecorded = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    OperationReport = table.Column<string>(type: "varchar(4000)", maxLength: 4000, nullable: true),
                    PostOpStatus = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SurgeryCases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SurgeryCases_Patients_PatientId",
                        column: x => x.PatientId,
                        principalTable: "Patients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SurgeryCases_Visits_VisitId",
                        column: x => x.VisitId,
                        principalTable: "Visits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Diagnoses",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    ConsultationId = table.Column<long>(type: "bigint", nullable: false),
                    Eye = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false),
                    Text = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Diagnoses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Diagnoses_Consultations_ConsultationId",
                        column: x => x.ConsultationId,
                        principalTable: "Consultations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "DiagnosticOrders",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    OrderNumber = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    PatientId = table.Column<long>(type: "bigint", nullable: false),
                    VisitId = table.Column<long>(type: "bigint", nullable: true),
                    TestId = table.Column<int>(type: "int", nullable: false),
                    RequestedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    Note = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),
                    Status = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiagnosticOrders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DiagnosticOrders_DiagnosticTests_TestId",
                        column: x => x.TestId,
                        principalTable: "DiagnosticTests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DiagnosticOrders_Patients_PatientId",
                        column: x => x.PatientId,
                        principalTable: "Patients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DiagnosticOrders_Visits_VisitId",
                        column: x => x.VisitId,
                        principalTable: "Visits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "MedicalPrescriptionItem",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    PrescriptionId = table.Column<long>(type: "bigint", nullable: false),
                    DrugName = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false),
                    Dose = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true),
                    Route = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true),
                    Duration = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true),
                    Instructions = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true),
                    Quantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false),
                    DispensedQuantity = table.Column<decimal>(type: "decimal(18,3)", precision: 18, scale: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MedicalPrescriptionItem", x => x.Id);
                    table.CheckConstraint("CK_RxItem_Dispensed", "`DispensedQuantity` >= 0 AND `DispensedQuantity` <= `Quantity`");
                    table.ForeignKey(
                        name: "FK_MedicalPrescriptionItem_MedicalPrescriptions_PrescriptionId",
                        column: x => x.PrescriptionId,
                        principalTable: "MedicalPrescriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "DiagnosticResults",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    OrderId = table.Column<long>(type: "bigint", nullable: false),
                    ResultText = table.Column<string>(type: "varchar(4000)", maxLength: 4000, nullable: false),
                    PerformedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    PerformedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiagnosticResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DiagnosticResults_DiagnosticOrders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "DiagnosticOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_ClinicalAccessGrants_UserId_PatientId_ExpiresAt",
                table: "ClinicalAccessGrants",
                columns: new[] { "UserId", "PatientId", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ClinicalAmendments_Entity_EntityId",
                table: "ClinicalAmendments",
                columns: new[] { "Entity", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_Consultations_VisitId",
                table: "Consultations",
                column: "VisitId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Diagnoses_ConsultationId",
                table: "Diagnoses",
                column: "ConsultationId");

            migrationBuilder.CreateIndex(
                name: "IX_DiagnosticOrders_OrderNumber",
                table: "DiagnosticOrders",
                column: "OrderNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DiagnosticOrders_PatientId",
                table: "DiagnosticOrders",
                column: "PatientId");

            migrationBuilder.CreateIndex(
                name: "IX_DiagnosticOrders_Status_Id",
                table: "DiagnosticOrders",
                columns: new[] { "Status", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_DiagnosticOrders_TestId",
                table: "DiagnosticOrders",
                column: "TestId");

            migrationBuilder.CreateIndex(
                name: "IX_DiagnosticOrders_VisitId",
                table: "DiagnosticOrders",
                column: "VisitId");

            migrationBuilder.CreateIndex(
                name: "IX_DiagnosticResults_OrderId",
                table: "DiagnosticResults",
                column: "OrderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DiagnosticTests_Name",
                table: "DiagnosticTests",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FollowUps_DueDate",
                table: "FollowUps",
                column: "DueDate");

            migrationBuilder.CreateIndex(
                name: "IX_FollowUps_PatientId",
                table: "FollowUps",
                column: "PatientId");

            migrationBuilder.CreateIndex(
                name: "IX_MedicalPrescriptionItem_PrescriptionId",
                table: "MedicalPrescriptionItem",
                column: "PrescriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_MedicalPrescriptions_PatientId",
                table: "MedicalPrescriptions",
                column: "PatientId");

            migrationBuilder.CreateIndex(
                name: "IX_MedicalPrescriptions_RxNumber",
                table: "MedicalPrescriptions",
                column: "RxNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MedicalPrescriptions_Status_Id",
                table: "MedicalPrescriptions",
                columns: new[] { "Status", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_MedicalPrescriptions_VisitId",
                table: "MedicalPrescriptions",
                column: "VisitId");

            migrationBuilder.CreateIndex(
                name: "IX_OpticalPrescriptions_PatientId",
                table: "OpticalPrescriptions",
                column: "PatientId");

            migrationBuilder.CreateIndex(
                name: "IX_OpticalPrescriptions_RxNumber",
                table: "OpticalPrescriptions",
                column: "RxNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OpticalPrescriptions_VisitId",
                table: "OpticalPrescriptions",
                column: "VisitId");

            migrationBuilder.CreateIndex(
                name: "IX_PreExaminations_VisitId",
                table: "PreExaminations",
                column: "VisitId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SurgeryCases_CaseNumber",
                table: "SurgeryCases",
                column: "CaseNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SurgeryCases_PatientId",
                table: "SurgeryCases",
                column: "PatientId");

            migrationBuilder.CreateIndex(
                name: "IX_SurgeryCases_Status_Id",
                table: "SurgeryCases",
                columns: new[] { "Status", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_SurgeryCases_VisitId",
                table: "SurgeryCases",
                column: "VisitId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClinicalAccessGrants");

            migrationBuilder.DropTable(
                name: "ClinicalAmendments");

            migrationBuilder.DropTable(
                name: "Diagnoses");

            migrationBuilder.DropTable(
                name: "DiagnosticResults");

            migrationBuilder.DropTable(
                name: "FollowUps");

            migrationBuilder.DropTable(
                name: "MedicalPrescriptionItem");

            migrationBuilder.DropTable(
                name: "OpticalPrescriptions");

            migrationBuilder.DropTable(
                name: "PreExaminations");

            migrationBuilder.DropTable(
                name: "SurgeryCases");

            migrationBuilder.DropTable(
                name: "Consultations");

            migrationBuilder.DropTable(
                name: "DiagnosticOrders");

            migrationBuilder.DropTable(
                name: "MedicalPrescriptions");

            migrationBuilder.DropTable(
                name: "DiagnosticTests");
        }
    }
}
