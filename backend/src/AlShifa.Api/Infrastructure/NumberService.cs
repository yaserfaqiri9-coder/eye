using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AlShifa.Api.Infrastructure;

/// <summary>
/// Issues gapless per-scope numbers. The row lock is held until the caller's transaction ends,
/// so a rollback also returns the number and two callers can never receive the same one.
/// </summary>
public sealed class NumberService(AppDbContext db)
{
    public async Task<long> NextAsync(string scope, string period = "", CancellationToken ct = default)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("NumberService must run inside a transaction.");
        }

        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction.GetDbTransaction();
        command.CommandText = """
            INSERT INTO NumberSequences (Scope, Period, Value) VALUES (@scope, @period, LAST_INSERT_ID(1))
            ON DUPLICATE KEY UPDATE Value = LAST_INSERT_ID(Value + 1);
            SELECT LAST_INSERT_ID();
            """;

        AddParameter(command, "@scope", scope);
        AddParameter(command, "@period", period);

        var result = await command.ExecuteScalarAsync(ct);
        return Convert.ToInt64(result);
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
