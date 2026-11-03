using System.Text.Json;
using AlShifa.Api.Features.Clinical;
using AlShifa.Api.Features.Visits;
using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AlShifa.Tests;

/// <summary>Rehearses the upgrade to Refractions and VisitTokens on a throwaway database holding records from before them.</summary>
public sealed class MigrationSafetyTests
{
    private const string BeforeNewFeatures = "20261101213721_SettingsAttachments";

    /// <summary>A database of its own, so the shared test database is not disturbed. Creating one needs the local root account.</summary>
    private static string ConnectionString()
    {
        var fromEnv = Environment.GetEnvironmentVariable("ALSHIFA_TEST_ADMIN_DB");
        if (!string.IsNullOrEmpty(fromEnv))
        {
            return fromEnv;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, ".tools", "dev-secrets.json")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("Set ALSHIFA_TEST_ADMIN_DB or run scripts/dev-db.ps1 first.");
        }

        using var secrets = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.FullName, ".tools", "dev-secrets.json")));
        return $"Server=127.0.0.1;Port=3307;Database=alshifa_migration_test;User=root;Password={secrets.RootElement.GetProperty("root").GetString()};";
    }

    private static Task<int> CountAsync(AppDbContext db, string table) =>
        db.Database.SqlQueryRaw<int>($"SELECT COUNT(*) AS Value FROM {table}").SingleAsync();

    [Fact]
    public async Task Records_survive_the_upgrade_and_a_hand_run_needs_the_backup_flag()
    {
        var connection = ConnectionString();
        Assert.True(DatabaseSafety.IsDisposable(connection));
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseMySQL(connection).Options);
        await db.Database.EnsureDeletedAsync();

        try
        {
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync(BeforeNewFeatures);

            await db.Database.ExecuteSqlRawAsync("INSERT INTO Departments (Id, Code, Name, IsActive) VALUES (1, 'doctor', 'داکتر', 1)");
            await db.Database.ExecuteSqlRawAsync("INSERT INTO Patients (Id, FileNumber, FirstName, Gender, CreatedAt, CreatedByUserId, Version) VALUES (1, 'P-000001', 'مریض قدیمی', 'Male', UTC_TIMESTAMP(6), 1, 3)");
            await db.Database.ExecuteSqlRawAsync("INSERT INTO Staff (Id, FullName, IsDoctor, IsActive, Version) VALUES (1, 'داکتر قدیمی', 1, 1, 0)");
            await db.Database.ExecuteSqlRawAsync("INSERT INTO Visits (Id, VisitNumber, PatientId, DepartmentId, DoctorStaffId, Status, VisitDate, QueueNumber, CreatedAt, CreatedByUserId, Version) VALUES (1, 'V-0000001', 1, 1, 1, 'InConsultation', '2026-11-01', 7, UTC_TIMESTAMP(6), 1, 2)");
            await db.Database.ExecuteSqlRawAsync("INSERT INTO Visits (Id, VisitNumber, PatientId, DepartmentId, DoctorId, Status, VisitDate, QueueNumber, CreatedAt, CreatedByUserId, Version) VALUES (2, 'V-0000002', 1, 1, 1, 'Completed', '2026-11-01', 8, UTC_TIMESTAMP(6), 1, 0)");
            await db.Database.ExecuteSqlRawAsync("INSERT INTO Appointments (Id, PatientId, DoctorStaffId, ScheduledAt, Status, CreatedByUserId, CreatedAt, Version) VALUES (1, 1, 1, '2026-12-01 05:00:00', 'Scheduled', 1, UTC_TIMESTAMP(6), 0)");
            await db.Database.ExecuteSqlRawAsync("INSERT INTO DiagnosticTests (Id, Name, IsActive) VALUES (1, 'آزمایش قدیمی', 1)");
            await db.Database.ExecuteSqlRawAsync("INSERT INTO DiagnosticOrders (Id, OrderNumber, PatientId, VisitId, TestId, RequestedByUserId, Status, CreatedAt, Version) VALUES (1, 'LAB-0000001', 1, 1, 1, 1, 'Requested', UTC_TIMESTAMP(6), 0)");

            // A database with records is not changed by a hand run, and the running application would say why.
            await Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseSafety.EnsureOperatorMayMigrateAsync(db, backupTaken: false));
            Assert.Contains("missing", await DatabaseSafety.SchemaProblemAsync(db));
            Assert.Contains(BeforeNewFeatures, await db.Database.GetAppliedMigrationsAsync());
            Assert.NotEmpty(await db.Database.GetPendingMigrationsAsync());

            await DatabaseSafety.EnsureOperatorMayMigrateAsync(db, backupTaken: true);
            await db.Database.MigrateAsync();
            Assert.Null(await DatabaseSafety.SchemaProblemAsync(db));

            var patient = await db.Patients.AsNoTracking().SingleAsync();
            Assert.Equal(("P-000001", "مریض قدیمی", 3), (patient.FileNumber, patient.FirstName, patient.Version));

            var visit = await db.Visits.AsNoTracking().SingleAsync(v => v.Id == 1);
            Assert.Equal((VisitStatus.InConsultation, 7, 2), (visit.Status, visit.QueueNumber, visit.Version));

            // Both old ways of naming a doctor end up on the one real link, which now resolves to the person.
            Assert.Equal("داکتر قدیمی", await db.Visits.Where(v => v.Id == 1).Select(v => v.Doctor!.FullName).SingleAsync());
            Assert.Equal(1, await db.Visits.Where(v => v.Id == 2).Select(v => v.DoctorStaffId).SingleAsync());
            Assert.Equal("داکتر قدیمی", await db.Appointments.Select(a => a.Doctor!.FullName).SingleAsync());

            // What existed before keeps meaning what it meant: an ordinary lab test, with no prepaid gate and no invoice of its own.
            Assert.Equal(DiagnosticKind.Lab, (await db.DiagnosticTests.AsNoTracking().SingleAsync()).Kind);
            var order = await db.DiagnosticOrders.AsNoTracking().SingleAsync();
            Assert.Equal((DiagnosticStatus.Requested, false, (long?)null), (order.Status, order.ReturnToDoctor, order.InvoiceId));
            Assert.Equal(0, await CountAsync(db, "VisitTokens"));
            Assert.Equal(0, await CountAsync(db, "Refractions"));

            // Down is exercised for the record only: production goes back through the backup, because Down drops the new tables.
            await migrator.MigrateAsync(BeforeNewFeatures);
            Assert.Equal(1, await CountAsync(db, "Patients"));
            Assert.Equal(2, await CountAsync(db, "Visits"));
            Assert.Equal(1, await CountAsync(db, "DiagnosticOrders"));

            await db.Database.MigrateAsync();
            Assert.Null(await DatabaseSafety.SchemaProblemAsync(db));
            Assert.Equal(1, await CountAsync(db, "Patients"));
        }
        finally
        {
            await db.Database.EnsureDeletedAsync();
        }
    }
}
