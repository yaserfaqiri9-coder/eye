using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;

namespace AlShifa.Api.Infrastructure;

/// <summary>
/// Keeps schema changes away from a database holding real records unless an operator asked for them after a backup.
/// MySQL cannot roll back DDL, so a failed or unwanted migration is undone only by restoring that backup.
/// </summary>
public static partial class DatabaseSafety
{
    /// <summary>Passed by update.ps1 once its backup has been made and verified.</summary>
    public const string BackupTakenFlag = "--backup-taken";

    /// <summary>Only databases that can be rebuilt from nothing may be migrated without being asked.</summary>
    public static bool IsDisposable(string connectionString) =>
        DisposableName().IsMatch(new MySqlConnectionStringBuilder(connectionString).Database ?? "");

    /// <summary>Null when the code and the database agree on the schema.</summary>
    public static string? SchemaProblem(IEnumerable<string> applied, IEnumerable<string> known)
    {
        var appliedSet = applied.ToHashSet();
        var knownSet = known.ToHashSet();

        var newer = appliedSet.Except(knownSet).Order().ToList();
        if (newer.Count > 0)
        {
            return $"The database is newer than this version of the application ({string.Join(", ", newer)}). Restore the backup taken before that update, or install the matching version.";
        }

        var pending = knownSet.Except(appliedSet).Order().ToList();
        return pending.Count > 0
            ? $"The database is missing migrations ({string.Join(", ", pending)}). Run deploy\\windows\\update.ps1, which takes a backup first."
            : null;
    }

    public static async Task<string?> SchemaProblemAsync(AppDbContext db, CancellationToken ct = default) =>
        SchemaProblem(await db.Database.GetAppliedMigrationsAsync(ct), db.Database.GetMigrations());

    /// <summary>For --migrate and --create-admin. An empty database has nothing to lose; any other needs the flag.</summary>
    public static async Task EnsureOperatorMayMigrateAsync(AppDbContext db, bool backupTaken, CancellationToken ct = default)
    {
        if (!await db.Database.CanConnectAsync(ct))
        {
            return;
        }

        var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToList();
        var newer = applied.Except(db.Database.GetMigrations()).ToList();
        if (newer.Count > 0)
        {
            throw new InvalidOperationException($"The database is newer than this version of the application ({string.Join(", ", newer)}). Nothing was changed.");
        }

        if (applied.Count > 0 && !backupTaken && (await db.Database.GetPendingMigrationsAsync(ct)).Any())
        {
            throw new InvalidOperationException($"This database already holds data and has pending migrations. Use deploy\\windows\\update.ps1, which takes a backup first and then passes {BackupTakenFlag}. Nothing was changed.");
        }
    }

    [GeneratedRegex("_(dev|test|load)$", RegexOptions.IgnoreCase)]
    private static partial Regex DisposableName();
}
