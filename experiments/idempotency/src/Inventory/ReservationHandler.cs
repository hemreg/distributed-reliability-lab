using Npgsql;

namespace Inventory;

public enum ProcessingOutcome
{
    /// <summary>The reservation was made and committed (stock reduced).</summary>
    Applied,

    /// <summary>Idempotent consumer only: this consumer had already applied the event; nothing was written.</summary>
    Duplicate,
}

public sealed record ProcessingResult(ProcessingOutcome Outcome, Guid? ReservationId, int? OnHandAfter);

/// <summary>
/// The business effect of one WorkOrderCreated delivery: reserve the part for the work order
/// (insert a part_reservations row) and take it off the shelf (stock_levels.on_hand -= quantity).
///
/// Naive (UNSAFE BASELINE): one transaction with the reservation and the stock update, for every
/// delivery. A second delivery of the same event reserves the part a second time.
///
/// Idempotent: the SAME transaction first inserts (consumer, event_id) into processed_messages with
/// ON CONFLICT DO NOTHING and inspects the row count:
///   1 row  -> first time for this consumer: make the reservation, update stock, COMMIT;
///   0 rows -> already applied: write nothing.
/// The primary key decides, not a prior SELECT, so two concurrent deliveries of the same event
/// cannot both pass: the second INSERT waits on the first transaction's uncommitted key, and gets
/// 0 rows once it commits (or 1 row, if it rolled back). Because the marker and the effect share one
/// transaction, a crash or rollback before COMMIT leaves neither behind, so a redelivery applies it.
///
/// This makes the DATABASE effect effectively-once for this consumer. Anything outside this
/// transaction (an e-mail, an HTTP call, a device command) is not covered.
/// </summary>
public sealed class ReservationHandler(
    NpgsqlDataSource db,
    InventoryOptions options,
    Func<WorkOrderCreated, CancellationToken, Task>? beforeCommit = null)
{
    public async Task<ProcessingResult> HandleAsync(WorkOrderCreated evt, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        if (options.Mode == ConsumerMode.Idempotent)
        {
            await using var mark = new NpgsqlCommand(
                """
                INSERT INTO processed_messages (consumer, event_id)
                VALUES (@consumer, @event_id)
                ON CONFLICT (consumer, event_id) DO NOTHING
                """, conn, tx);
            mark.Parameters.AddWithValue("consumer", options.ConsumerName);
            mark.Parameters.AddWithValue("event_id", evt.EventId);
            if (await mark.ExecuteNonQueryAsync(ct) == 0)
            {
                await tx.RollbackAsync(ct); // nothing was written; ending the transaction either way is fine
                return new ProcessingResult(ProcessingOutcome.Duplicate, null, null);
            }
        }

        var reservationId = Guid.CreateVersion7();
        await using (var reserve = new NpgsqlCommand(
            """
            INSERT INTO part_reservations (reservation_id, consumer, event_id, work_order_id, operation_id, part_no, quantity)
            VALUES (@reservation_id, @consumer, @event_id, @work_order_id, @operation_id, @part_no, @quantity)
            """, conn, tx))
        {
            reserve.Parameters.AddWithValue("reservation_id", reservationId);
            reserve.Parameters.AddWithValue("consumer", options.ConsumerName);
            reserve.Parameters.AddWithValue("event_id", evt.EventId);
            reserve.Parameters.AddWithValue("work_order_id", evt.WorkOrderId);
            reserve.Parameters.AddWithValue("operation_id", evt.OperationId);
            reserve.Parameters.AddWithValue("part_no", evt.PartNo);
            reserve.Parameters.AddWithValue("quantity", evt.Quantity);
            await reserve.ExecuteNonQueryAsync(ct);
        }

        int onHand;
        await using (var take = new NpgsqlCommand(
            """
            UPDATE stock_levels SET on_hand = on_hand - @quantity
            WHERE consumer = @consumer AND part_no = @part_no
            RETURNING on_hand
            """, conn, tx))
        {
            take.Parameters.AddWithValue("quantity", evt.Quantity);
            take.Parameters.AddWithValue("consumer", options.ConsumerName);
            take.Parameters.AddWithValue("part_no", evt.PartNo);
            onHand = await take.ExecuteScalarAsync(ct) is int value
                ? value
                : throw new InvalidOperationException($"No stock level for part {evt.PartNo} (consumer {options.ConsumerName}).");
        }

        // Test seam only (holds or fails the transaction in tests); null in the running service.
        if (beforeCommit is not null) await beforeCommit(evt, ct);

        await tx.CommitAsync(ct);
        return new ProcessingResult(ProcessingOutcome.Applied, reservationId, onHand);
    }

    /// <summary>Seeds this consumer's stock level for the seal kit once. Never resets an existing level.</summary>
    public async Task EnsureStockAsync(string partNo, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            """
            INSERT INTO stock_levels (consumer, part_no, initial_on_hand, on_hand)
            VALUES (@consumer, @part_no, @initial, @initial)
            ON CONFLICT (consumer, part_no) DO NOTHING
            """);
        cmd.Parameters.AddWithValue("consumer", options.ConsumerName);
        cmd.Parameters.AddWithValue("part_no", partNo);
        cmd.Parameters.AddWithValue("initial", options.InitialStock);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
