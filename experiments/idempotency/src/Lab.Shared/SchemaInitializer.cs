using System.Reflection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Lab.Shared;

public static class SchemaInitializer
{
    /// <summary>
    /// Applies an embedded, idempotent SQL script inside one transaction. An advisory lock
    /// serializes concurrent initializers (several services and the test runner start at the
    /// same time), so CREATE ... IF NOT EXISTS cannot race.
    /// </summary>
    public static async Task ApplyAsync(NpgsqlDataSource db, ILogger logger, Assembly assembly, string resourceName,
        CancellationToken ct = default)
    {
        await using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded {resourceName} not found.");
        var sql = await new StreamReader(stream).ReadToEndAsync(ct);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var conn = await db.OpenConnectionAsync(ct);
                await using var tx = await conn.BeginTransactionAsync(ct);
                // One lock for all E02 schemas: they live in the same database.
                await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext('e02-schema'))", conn, tx))
                    await lockCmd.ExecuteNonQueryAsync(ct);
                await using (var cmd = new NpgsqlCommand(sql, conn, tx))
                    await cmd.ExecuteNonQueryAsync(ct);
                await tx.CommitAsync(ct);
                logger.LogInformation("phase={phase} schema={schema} Schema applied", "schema_ready", resourceName);
                return;
            }
            catch (NpgsqlException ex) when (attempt < 15 && ex.IsTransient)
            {
                logger.LogWarning(ex, "phase={phase} attempt={attempt} PostgreSQL not reachable yet, retrying", "schema_wait", attempt);
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
    }
}
