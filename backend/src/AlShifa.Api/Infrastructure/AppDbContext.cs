using AlShifa.Api.Features.Clinical;
using AlShifa.Api.Features.Calls;
using AlShifa.Api.Features.Finance;
using AlShifa.Api.Features.Inventory;
using AlShifa.Api.Features.Patients;
using AlShifa.Api.Features.Settings;
using AlShifa.Api.Features.Users;
using AlShifa.Api.Features.Visits;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AlShifa.Api.Infrastructure;

public class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser, AppRole, long>(options)
{
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Staff> Staff => Set<Staff>();
    public DbSet<Patient> Patients => Set<Patient>();
    public DbSet<PatientChangeLog> PatientChangeLogs => Set<PatientChangeLog>();
    public DbSet<Visit> Visits => Set<Visit>();
    public DbSet<VisitToken> VisitTokens => Set<VisitToken>();
    public DbSet<VisitStatusHistory> VisitStatusHistory => Set<VisitStatusHistory>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<OutboxEvent> OutboxEvents => Set<OutboxEvent>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<NumberSequence> NumberSequences => Set<NumberSequence>();
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<PreExamination> PreExaminations => Set<PreExamination>();
    public DbSet<Refraction> Refractions => Set<Refraction>();
    public DbSet<RefractionLine> RefractionLines => Set<RefractionLine>();
    public DbSet<Consultation> Consultations => Set<Consultation>();
    public DbSet<Diagnosis> Diagnoses => Set<Diagnosis>();
    public DbSet<ClinicalAmendment> ClinicalAmendments => Set<ClinicalAmendment>();
    public DbSet<MedicalPrescription> MedicalPrescriptions => Set<MedicalPrescription>();
    public DbSet<OpticalPrescription> OpticalPrescriptions => Set<OpticalPrescription>();
    public DbSet<FollowUp> FollowUps => Set<FollowUp>();
    public DbSet<ClinicalAccessGrant> ClinicalAccessGrants => Set<ClinicalAccessGrant>();
    public DbSet<DiagnosticTest> DiagnosticTests => Set<DiagnosticTest>();
    public DbSet<DiagnosticOrder> DiagnosticOrders => Set<DiagnosticOrder>();
    public DbSet<DiagnosticResult> DiagnosticResults => Set<DiagnosticResult>();
    public DbSet<SurgeryCase> SurgeryCases => Set<SurgeryCase>();
    public DbSet<Service> Services => Set<Service>();
    public DbSet<ServicePrice> ServicePrices => Set<ServicePrice>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceItem> InvoiceItems => Set<InvoiceItem>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Refund> Refunds => Set<Refund>();
    public DbSet<CashRegister> CashRegisters => Set<CashRegister>();
    public DbSet<CashSession> CashSessions => Set<CashSession>();
    public DbSet<LedgerAccount> LedgerAccounts => Set<LedgerAccount>();
    public DbSet<LedgerTransaction> LedgerTransactions => Set<LedgerTransaction>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();
    public DbSet<Expense> Expenses => Set<Expense>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<StockLocation> StockLocations => Set<StockLocation>();
    public DbSet<StockBatch> StockBatches => Set<StockBatch>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<Purchase> Purchases => Set<Purchase>();
    public DbSet<Sale> Sales => Set<Sale>();
    public DbSet<OpticalOrder> OpticalOrders => Set<OpticalOrder>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<PatientCall> PatientCalls => Set<PatientCall>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<decimal>().HavePrecision(18, 2);
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeConverter>();
        configurationBuilder.Properties<Enum>().HaveConversion<string>().HaveMaxLength(30);

        // MySql.EntityFrameworkCore 10.0.12 fails to materialize DateOnly from a DATE column without this converter.
        configurationBuilder.Properties<DateOnly>().HaveConversion<DateOnlyConverter>().HaveColumnType("date");
        configurationBuilder.Properties<DateOnly?>().HaveConversion<NullableDateOnlyConverter>().HaveColumnType("date");
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        ConfigureClinical(b);
        ConfigureFinance(b);
        ConfigureInventory(b);
        b.Entity<Attachment>().HasIndex(x => new { x.OwnerType, x.OwnerId });

        // Medical and financial history must never disappear through a cascading delete.
        foreach (var fk in b.Model.GetEntityTypes().SelectMany(e => e.GetForeignKeys()))
        {
            if (!fk.DeclaringEntityType.ClrType.Namespace!.StartsWith("Microsoft.AspNetCore.Identity"))
            {
                fk.DeleteBehavior = DeleteBehavior.Restrict;
            }
        }

        b.Entity<Department>().HasIndex(x => x.Code).IsUnique();
        b.Entity<Department>().Property(x => x.Version).IsConcurrencyToken();
        b.Entity<Supplier>().HasIndex(x => x.Name).IsUnique();
        b.Entity<Supplier>().Property(x => x.Version).IsConcurrencyToken();
        b.Entity<Purchase>().HasOne(x => x.Supplier).WithMany().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Restrict);

        b.Entity<Staff>(e =>
        {
            e.Property(x => x.Avatar).HasDefaultValue(12);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => x.UserId).IsUnique();
            e.HasIndex(x => x.FullName);
        });

        b.Entity<Patient>(e =>
        {
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => x.FileNumber).IsUnique();
            e.HasIndex(x => new { x.FirstName, x.LastName });
            e.HasIndex(x => x.Phone);
        });

        b.Entity<PatientChangeLog>().HasIndex(x => new { x.PatientId, x.ChangedAt });

        b.Entity<Visit>(e =>
        {
            e.Property(x => x.Version).IsConcurrencyToken();

            // By name alone EF would not link Doctor to DoctorStaffId: it would add an empty column of its own and no visit would have a doctor.
            e.HasOne(x => x.Doctor).WithMany().HasForeignKey(x => x.DoctorStaffId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.VisitNumber).IsUnique();
            e.HasIndex(x => new { x.VisitDate, x.DepartmentId, x.QueueNumber }).IsUnique();
            e.HasIndex(x => new { x.PatientId, x.VisitDate });
            e.HasIndex(x => new { x.VisitDate, x.Status });

            // NULL once the visit is closed, so a patient can return to the same department on the same day.
            e.Property<string?>("ActiveVisitKey")
                .HasMaxLength(60)
                .HasComputedColumnSql("CASE WHEN `Status` NOT IN ('Completed','Cancelled') THEN CONCAT(`PatientId`,'|',`DepartmentId`,'|',`VisitDate`) END", stored: true);
            e.HasIndex("ActiveVisitKey").IsUnique();
        });

        b.Entity<VisitToken>(e =>
        {
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => x.InvoiceId).IsUnique();
            e.HasIndex(x => x.VisitId).IsUnique();
            e.HasIndex(x => x.TokenDate);
            e.HasOne(x => x.Doctor).WithMany().HasForeignKey(x => x.DoctorStaffId);
        });

        b.Entity<VisitStatusHistory>().HasIndex(x => new { x.VisitId, x.ChangedAt });

        b.Entity<PatientCall>(e =>
        {
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => new { x.Status, x.Id });
            e.HasIndex(x => new { x.VisitId, x.Desk, x.Id });
            e.HasIndex(x => new { x.VisitTokenId, x.Desk, x.Id });
            e.HasIndex(x => x.RequestedAt);
        });

        b.Entity<Appointment>(e =>
        {
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasOne(x => x.Doctor).WithMany().HasForeignKey(x => x.DoctorStaffId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.DoctorStaffId, x.ScheduledAt });
            e.HasIndex(x => x.PatientId);

            e.Property<string?>("ActiveSlotKey")
                .HasMaxLength(60)
                .HasComputedColumnSql("CASE WHEN `Status` = 'Scheduled' THEN CONCAT(`DoctorStaffId`,'|',DATE_FORMAT(`ScheduledAt`,'%Y%m%d%H%i')) END", stored: true);
            e.HasIndex("ActiveSlotKey").IsUnique();
        });

        b.Entity<AuditLog>(e =>
        {
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => new { x.Entity, x.EntityId });
        });

        b.Entity<OutboxEvent>().HasIndex(x => x.ProcessedAt);

        b.Entity<IdempotencyRecord>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.Key }).IsUnique();
            e.Property(x => x.ResponseBody).HasColumnType("text");
        });

        b.Entity<NumberSequence>().HasKey(x => new { x.Scope, x.Period });

        b.Entity<Setting>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Version).IsConcurrencyToken();
        });
    }

    private static void ConfigureFinance(ModelBuilder b)
    {
        b.Entity<Service>().HasIndex(x => x.Code).IsUnique();
        b.Entity<ServicePrice>().HasIndex(x => new { x.ServiceId, x.EffectiveFrom });

        b.Entity<Invoice>(e =>
        {
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => x.InvoiceNumber).IsUnique();
            e.HasIndex(x => new { x.Status, x.Id });
            e.HasIndex(x => x.VisitId);
            e.HasIndex(x => x.CreatedAt);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Invoice_Amounts", "`Total` >= 0 AND `Discount` >= 0 AND `Paid` >= 0 AND `Refunded` >= 0");
                t.HasCheckConstraint("CK_Invoice_NotOverpaid", "`Paid` + `Discount` <= `Total`");
            });
        });

        b.Entity<InvoiceItem>(e =>
        {
            e.Property(x => x.Quantity).HasPrecision(18, 3);

            // A billable source can have only one live line, which is what stops a service being charged twice.
            e.Property<string?>("ActiveSourceKey")
                .HasMaxLength(60)
                .HasComputedColumnSql("CASE WHEN `IsVoid` = 0 AND `SourceType` IS NOT NULL THEN CONCAT(`SourceType`,':',`SourceId`) END", stored: true);
            e.HasIndex("ActiveSourceKey").IsUnique();
        });

        b.Entity<Payment>(e =>
        {
            e.HasIndex(x => x.ReceiptNumber).IsUnique();
            e.HasIndex(x => x.CreatedAt);
            e.ToTable(t => t.HasCheckConstraint("CK_Payment_Amounts", "`Amount` > 0 AND `Refunded` >= 0 AND `Refunded` <= `Amount`"));
        });

        b.Entity<Refund>().HasIndex(x => x.RefundNumber).IsUnique();

        b.Entity<CashSession>(e =>
        {
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => x.OpenedByUserId);
            e.Property<int?>("OpenRegisterKey")
                .HasComputedColumnSql("CASE WHEN `ClosedAt` IS NULL THEN `RegisterId` END", stored: true);
            e.HasIndex("OpenRegisterKey").IsUnique();
        });

        b.Entity<LedgerTransaction>(e =>
        {
            e.HasIndex(x => new { x.Kind, x.SourceType, x.SourceId }).IsUnique();
            e.HasIndex(x => x.CreatedAt);
            e.HasMany(x => x.Entries).WithOne().HasForeignKey(x => x.TransactionId);
        });

        b.Entity<LedgerEntry>(e =>
        {
            e.HasOne(x => x.Account).WithMany().HasForeignKey(x => x.AccountCode);
            e.HasIndex(x => x.AccountCode);
            e.ToTable(t => t.HasCheckConstraint("CK_Ledger_OneSided", "(`Debit` >= 0 AND `Credit` >= 0) AND (`Debit` = 0 OR `Credit` = 0)"));
        });

        b.Entity<Expense>(e =>
        {
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => x.ExpenseNumber).IsUnique();
            e.HasIndex(x => x.ExpenseDate);
        });
    }

    private static void ConfigureInventory(ModelBuilder b)
    {
        b.Entity<Product>(e =>
        {
            e.Property(x => x.Version).IsConcurrencyToken();
            e.Property(x => x.ReorderLevel).HasPrecision(18, 3);
            e.HasIndex(x => x.Code).IsUnique();
            e.HasIndex(x => x.Name);
        });

        b.Entity<StockLocation>().HasIndex(x => x.Code).IsUnique();

        b.Entity<StockBatch>(e =>
        {
            e.Property(x => x.Quantity).HasPrecision(18, 3);
            e.HasIndex(x => new { x.ProductId, x.LocationId, x.ExpiryDate });
            e.ToTable(t => t.HasCheckConstraint("CK_StockBatch_NotNegative", "`Quantity` >= 0"));
        });

        b.Entity<StockMovement>(e =>
        {
            e.Property(x => x.Quantity).HasPrecision(18, 3);
            e.HasIndex(x => x.CreatedAt);
        });

        b.Entity<Purchase>(e =>
        {
            e.HasIndex(x => x.PurchaseNumber).IsUnique();
            e.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.PurchaseId);
        });

        b.Entity<PurchaseItem>().Property(x => x.Quantity).HasPrecision(18, 3);

        b.Entity<Sale>(e =>
        {
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => x.SaleNumber).IsUnique();
            e.HasIndex(x => x.CreatedAt);
            e.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.SaleId);
        });

        b.Entity<SaleItem>().Property(x => x.Quantity).HasPrecision(18, 3);

        b.Entity<OpticalOrder>(e =>
        {
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => x.OrderNumber).IsUnique();
            e.HasIndex(x => new { x.Status, x.Id });
        });
    }

    private static void ConfigureClinical(ModelBuilder b)
    {
        b.Entity<PreExamination>(e =>
        {
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => x.VisitId).IsUnique();
        });

        b.Entity<Refraction>(e =>
        {
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => x.VisitId).IsUnique();
            e.HasMany(x => x.Lines).WithOne().HasForeignKey(x => x.RefractionId);
        });

        b.Entity<RefractionLine>().HasIndex(x => new { x.RefractionId, x.Stage, x.Eye }).IsUnique();

        b.Entity<Consultation>(e =>
        {
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => x.VisitId).IsUnique();
        });

        b.Entity<Diagnosis>().HasIndex(x => x.ConsultationId);
        b.Entity<ClinicalAmendment>().HasIndex(x => new { x.Entity, x.EntityId });

        b.Entity<MedicalPrescription>(e =>
        {
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => x.RxNumber).IsUnique();
            e.HasIndex(x => new { x.Status, x.Id });
            e.HasIndex(x => x.VisitId);
            e.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.PrescriptionId);
        });

        b.Entity<MedicalPrescriptionItem>(e =>
        {
            e.Property(x => x.Quantity).HasPrecision(18, 3);
            e.Property(x => x.DispensedQuantity).HasPrecision(18, 3);
            e.ToTable(t => t.HasCheckConstraint("CK_RxItem_Dispensed", "`DispensedQuantity` >= 0 AND `DispensedQuantity` <= `Quantity`"));
        });

        b.Entity<OpticalPrescription>(e =>
        {
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => x.RxNumber).IsUnique();
            e.HasIndex(x => x.VisitId);
        });

        b.Entity<FollowUp>().HasIndex(x => x.DueDate);
        b.Entity<ClinicalAccessGrant>().HasIndex(x => new { x.UserId, x.PatientId, x.ExpiresAt });
        b.Entity<DiagnosticTest>().HasIndex(x => x.Name).IsUnique();

        b.Entity<DiagnosticOrder>(e =>
        {
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => x.OrderNumber).IsUnique();
            e.HasIndex(x => new { x.Status, x.Id });
            e.HasIndex(x => x.VisitId);
            e.HasOne(x => x.Result).WithOne(x => x.Order).HasForeignKey<DiagnosticResult>(x => x.OrderId);
        });

        b.Entity<DiagnosticResult>().HasIndex(x => x.OrderId).IsUnique();

        b.Entity<SurgeryCase>(e =>
        {
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => x.CaseNumber).IsUnique();
            e.HasIndex(x => new { x.Status, x.Id });
        });
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        BumpVersions();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        BumpVersions();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void BumpVersions()
    {
        foreach (var entry in ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified))
        {
            // DATETIME(6) keeps microseconds, so a response built from the entity must not carry more than the stored row.
            foreach (var property in entry.Properties.Where(p => p.CurrentValue is DateTime { Ticks: var ticks } && ticks % TimeSpan.TicksPerMicrosecond != 0))
            {
                var value = (DateTime)property.CurrentValue!;
                property.CurrentValue = value.AddTicks(-(value.Ticks % TimeSpan.TicksPerMicrosecond));
            }

            if (entry.State == EntityState.Modified && entry.Metadata.FindProperty("Version") is { IsConcurrencyToken: true })
            {
                var property = entry.Property("Version");
                property.CurrentValue = (int)property.OriginalValue! + 1;
            }
        }
    }

    private sealed class DateOnlyConverter()
        : ValueConverter<DateOnly, DateTime>(
            v => v.ToDateTime(TimeOnly.MinValue),
            v => DateOnly.FromDateTime(v));

    private sealed class NullableDateOnlyConverter()
        : ValueConverter<DateOnly?, DateTime?>(
            v => v == null ? null : v.Value.ToDateTime(TimeOnly.MinValue),
            v => v == null ? null : DateOnly.FromDateTime(v.Value));

    private sealed class UtcDateTimeConverter()
        : ValueConverter<DateTime, DateTime>(
            v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
            v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private sealed class NullableUtcDateTimeConverter()
        : ValueConverter<DateTime?, DateTime?>(
            v => v == null ? v : v.Value.Kind == DateTimeKind.Utc ? v : v.Value.ToUniversalTime(),
            v => v == null ? v : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc));
}
