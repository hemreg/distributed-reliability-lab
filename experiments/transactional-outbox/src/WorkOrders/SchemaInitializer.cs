using Npgsql;

namespace WorkOrders;

public static class SchemaInitializer
{
    public static async Task ApplyAsync(NpgsqlDataSource db, ILogger logger, CancellationToken ct = default)
    {
        await using var stream = typeof(SchemaInitializer).Assembly.GetManifestResourceStream("WorkOrders.schema.sql")
            ?? throw new InvalidOperationException("Embedded schema.sql not found.");
        var sql = await new StreamReader(stream).ReadToEndAsync(ct);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var conn = await db.OpenConnectionAsync(ct);
                await using var tx = await conn.BeginTransactionAsync(ct);
                // Serialize concurrent initializers (API + test runner) so IF NOT EXISTS cannot race.
                await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext('e01-schema'))", conn, tx))
                    await lockCmd.ExecuteNonQueryAsync(ct);
                await using (var cmd = new NpgsqlCommand(sql, conn, tx))
                    await cmd.ExecuteNonQueryAsync(ct);
                await tx.CommitAsync(ct);
                logger.LogInformation("phase={phase} Schema applied", "schema_ready");
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
