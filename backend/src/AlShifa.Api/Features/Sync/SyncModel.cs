using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using AlShifa.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace AlShifa.Api.Features.Sync;

/// <summary>
/// How this copy of the application runs. Set once at startup from configuration.
/// The hospital server captures changes for the online copy (Capture); the online copy only shows them (ReadOnly).
/// </summary>
public sealed class SyncSwitch
{
    public bool Capture { get; init; }

    public bool ReadOnly { get; init; }

    /// <summary>Set only by the --online-access operator command, a separate process at the server console.</summary>
    public bool OperatorWrites { get; set; }

    public static SyncSwitch From(IConfiguration config) => new()
    {
        ReadOnly = config.GetValue("Mirror:Enabled", false),
        Capture = !config.GetValue("Mirror:Enabled", false) && !string.IsNullOrWhiteSpace(config["Sync:Url"]),
    };
}

/// <summary>A row that changed on the hospital server and is not yet confirmed by the online copy.
/// Written in the same transaction as the change itself, so a committed change always has its entry.</summary>
public class SyncChange
{
    public long Id { get; set; }

    [MaxLength(64)]
    public string TableName { get; set; } = "";

    /// <summary>The primary key as a JSON array of column values.</summary>
    [MaxLength(300)]
    public string KeyJson { get; set; } = "";

    public DateTime CreatedAt { get; set; }
}

/// <summary>Progress and health of the sync, on both sides. Never copied to the online database.</summary>
public class SyncState
{
    [MaxLength(40)]
    public string Key { get; set; } = "";

    [MaxLength(2000)]
    public string? Value { get; set; }

    public DateTime UpdatedAt { get; set; }
}

/// <summary>What is copied to the online database, and what never leaves the hospital.</summary>
public static class SyncSchema
{
    /// <summary>Sign-in secrets, sessions, internal queues and counters: the online copy has no use for them.</summary>
    public static readonly HashSet<string> LocalOnlyTables = new(StringComparer.OrdinalIgnoreCase)
    {
        "SyncChanges", "SyncStates", "OutboxEvents", "IdempotencyRecords", "NumberSequences", "AspNetUserTokens", "AspNetUserLogins",
    };

    /// <summary>Passwords and lockout stay at the hospital. Online sign-in has its own password and second factor, set on the online server.</summary>
    public static readonly Dictionary<string, HashSet<string>> LocalOnlyColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AspNetUsers"] = new(StringComparer.OrdinalIgnoreCase)
        {
            "PasswordHash", "SecurityStamp", "ConcurrencyStamp", "LockoutEnd", "LockoutEnabled", "AccessFailedCount", "TwoFactorEnabled",
        },
    };

    /// <summary>Values for local-only columns when a row is first created online. A synced account cannot sign in until an operator enables it there.</summary>
    public static object? InsertDefault(string table, IColumn column) => (table, column.Name) switch
    {
        ("AspNetUsers", "SecurityStamp" or "ConcurrencyStamp") => Guid.NewGuid().ToString(),
        ("AspNetUsers", "LockoutEnabled") => true,
        ("AspNetUsers", "AccessFailedCount") => 0,
        ("AspNetUsers", "TwoFactorEnabled") => false,
        _ => null,
    };

    public static bool IsSynced(string? table) => table is not null && !LocalOnlyTables.Contains(table);

    public static bool IsSyncedColumn(string table, string column) =>
        !(LocalOnlyColumns.TryGetValue(table, out var columns) && columns.Contains(column));

    public static IEnumerable<ITable> Tables(DbContext db) =>
        db.Model.GetRelationalModel().Tables.Where(t => IsSynced(t.Name)).OrderBy(t => t.Name, StringComparer.Ordinal);

    public static IReadOnlyList<IColumn> SyncedColumns(ITable table) =>
        table.Columns.Where(c => IsSyncedColumn(table.Name, c.Name)).OrderBy(c => c.Name, StringComparer.Ordinal).ToList();

    public static IReadOnlyList<IColumn> KeyColumns(ITable table) => table.PrimaryKey!.Columns.ToList();

    /// <summary>The newest migration this code knows. Both sides must agree before rows are copied.</summary>
    public static string SchemaVersion(DbContext db) =>
        db.Database.GetMigrations().LastOrDefault() ?? "";

    public static string Quote(string identifier) => "`" + identifier.Replace("`", "``") + "`";

    /// <summary>Children before parents, so rows can be deleted with foreign key checks on.</summary>
    public static List<ITable> ChildrenFirst(IEnumerable<ITable> tables)
    {
        var all = tables.ToList();
        var names = all.Select(t => t.Name).ToHashSet();
        var ordered = new List<ITable>();
        var done = new HashSet<string>();
        var visiting = new HashSet<string>();

        void Visit(ITable table)
        {
            if (done.Contains(table.Name) || !visiting.Add(table.Name))
            {
                return;
            }

            // Tables that point at this one go first.
            foreach (var child in all.Where(c => c.Name != table.Name && c.ForeignKeyConstraints.Any(fk => fk.PrincipalTable.Name == table.Name)))
            {
                Visit(child);
            }

            visiting.Remove(table.Name);
            done.Add(table.Name);
            ordered.Add(table);
        }

        foreach (var table in all.Where(t => names.Contains(t.Name)))
        {
            Visit(table);
        }

        return ordered;
    }
}

/// <summary>Records which rows a SaveChanges call touched, inside the same database transaction.</summary>
internal static class SyncCapture
{
    internal sealed record Pending(EntityEntry Entry, string Table, IReadOnlyList<IProperty> Key, string? KnownKey);

    public static List<Pending> Collect(ChangeTracker tracker)
    {
        var pending = new List<Pending>();
        foreach (var entry in tracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            var table = entry.Metadata.GetTableName();
            if (!SyncSchema.IsSynced(table) || entry.Metadata.FindPrimaryKey() is not { } key)
            {
                continue;
            }

            // A generated key is known only after the insert; every other key is read now, while a deleted entry is still tracked.
            pending.Add(new Pending(entry, table!, key.Properties, entry.State == EntityState.Added ? null : KeyJson(entry, key.Properties)));
        }

        return pending;
    }

    public static string KeyJson(EntityEntry entry, IReadOnlyList<IProperty> key) =>
        JsonSerializer.Serialize(key.Select(p =>
        {
            var value = entry.Property(p.Name).CurrentValue;
            return value is null ? null : p.GetTypeMapping().Converter?.ConvertToProvider(value) ?? value;
        }));

    public static (string Sql, object[] Parameters)? InsertSql(IEnumerable<Pending> pending)
    {
        var rows = pending
            .Select(p => (p.Table, Key: p.KnownKey ?? KeyJson(p.Entry, p.Key)))
            .Distinct()
            .ToList();
        if (rows.Count == 0)
        {
            return null;
        }

        var values = string.Join(",", rows.Select((_, i) => $"({{{i * 2}}},{{{i * 2 + 1}}},UTC_TIMESTAMP(6))"));
        var parameters = rows.SelectMany(r => new object[] { r.Table, r.Key }).ToArray();
        return ($"INSERT INTO `SyncChanges` (`TableName`,`KeyJson`,`CreatedAt`) VALUES {values}", parameters);
    }

    /// <summary>The online copy may update its own sign-in counters and nothing else.</summary>
    public static void EnsureReadOnlyAllowed(ChangeTracker tracker)
    {
        foreach (var entry in tracker.Entries())
        {
            if (entry.State is EntityState.Unchanged or EntityState.Detached)
            {
                continue;
            }

            if (entry.State != EntityState.Modified || entry.Metadata.ClrType != typeof(Users.AppUser))
            {
                throw ReadOnlyMirror.Refusal();
            }

            // Identity marks every column modified. Only the ones that really changed are written, which the database account allows.
            foreach (var property in entry.Properties.Where(p => p.IsModified))
            {
                if (Equals(property.OriginalValue, property.CurrentValue))
                {
                    property.IsModified = false;
                }
                else if (property.Metadata.Name is not ("AccessFailedCount" or "LockoutEnd" or "ConcurrencyStamp"))
                {
                    throw ReadOnlyMirror.Refusal();
                }
            }
        }
    }
}
