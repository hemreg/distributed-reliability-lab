using Npgsql;
using NpgsqlTypes;

namespace WorkOrders;

public sealed record CreateWorkOrderResult(Guid WorkOrderId, Guid EventId, string OperationId, string EventStatus);

/// <summary>
/// Transactional outbox write path (E01's mitigation): the work order and the outbox row commit
/// atomically. The request does no broker I/O; the outbox worker relays pending rows later.
/// </summary>
public sealed class WorkOrderService(NpgsqlDataSource db, ILogger<WorkOrderService> logger)
{
    public async Task<CreateWorkOrderResult> CreateAsync(string title, string operationId, CancellationToken ct = default)
    {
        var evt = new WorkOrderCreated(Guid.CreateVersion7(), Guid.CreateVersion7(), title, operationId,
            WorkOrderCreated.SealKit, 1, DateTimeOffset.UtcNow);

        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["operation_id"] = operationId,
            ["work_order_id"] = evt.WorkOrderId,
            ["event_id"] = evt.EventId,
        });

        await using (var conn = await db.OpenConnectionAsync(ct))
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            await using (var cmd = new NpgsqlCommand(
                """
                INSERT INTO work_orders (id, title, operation_id, part_no, quantity, created_at)
                VALUES (@id, @title, @operation_id, @part_no, @quantity, @created_at)
                """, conn, tx))
            {
                cmd.Parameters.AddWithValue("id", evt.WorkOrderId);
                cmd.Parameters.AddWithValue("title", evt.Title);
                cmd.Parameters.AddWithValue("operation_id", evt.OperationId);
                cmd.Parameters.AddWithValue("part_no", evt.PartNo);
                cmd.Parameters.AddWithValue("quantity", evt.Quantity);
                cmd.Parameters.AddWithValue("created_at", evt.OccurredAt);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await using (var cmd = new NpgsqlCommand(
                """
                INSERT INTO outbox_messages (event_id, event_type, aggregate_id, operation_id, payload)
                VALUES (@event_id, @event_type, @aggregate_id, @operation_id, @payload)
                """, conn, tx))
            {
                cmd.Parameters.AddWithValue("event_id", evt.EventId);
                cmd.Parameters.AddWithValue("event_type", WorkOrderCreated.EventType);
                cmd.Parameters.AddWithValue("aggregate_id", evt.WorkOrderId);
                cmd.Parameters.AddWithValue("operation_id", evt.OperationId);
                cmd.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, evt.ToJson());
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        logger.LogInformation("phase={phase} outbox_status={outbox_status} Work order and outbox event committed atomically",
            "db_committed", "pending");

        return new CreateWorkOrderResult(evt.WorkOrderId, evt.EventId, evt.OperationId, "pending");
    }
}
