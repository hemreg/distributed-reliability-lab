using Npgsql;
using NpgsqlTypes;

namespace WorkOrders;

public sealed record CreateWorkOrderResult(WriteMode Mode, Guid WorkOrderId, Guid EventId, string OperationId, string EventStatus);

/// <summary>Raised by the naive path when COMMIT succeeded but the publish did not.</summary>
public sealed class EventNotPublishedException(Guid workOrderId, Guid eventId, Exception inner)
    : Exception("The work order was committed, but WorkOrderCreated could not be published.", inner)
{
    public Guid WorkOrderId { get; } = workOrderId;
    public Guid EventId { get; } = eventId;
}

public sealed class WorkOrderService(
    NpgsqlDataSource db,
    Broker broker,
    IProcessTerminator terminator,
    ILogger<WorkOrderService> logger)
{
    public async Task<CreateWorkOrderResult> CreateAsync(
        WriteMode mode, string title, string operationId, InjectedFault fault, CancellationToken ct = default)
    {
        var evt = new WorkOrderCreated(Guid.CreateVersion7(), Guid.CreateVersion7(), title, operationId, DateTimeOffset.UtcNow);

        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["mode"] = mode.ToString().ToLowerInvariant(),
            ["operation_id"] = operationId,
            ["work_order_id"] = evt.WorkOrderId,
            ["event_id"] = evt.EventId,
        });

        return mode == WriteMode.Naive
            ? await CreateNaiveAsync(evt, fault, ct)
            : await CreateWithOutboxAsync(evt, fault, ct);
    }

    // ---------------------------------------------------------------------------------------
    // UNSAFE BASELINE (dual write). Two independent systems, two independent steps:
    //   1. COMMIT the work order in PostgreSQL
    //   2. publish WorkOrderCreated to RabbitMQ
    // Anything that stops the process (or fails the publish) between 1 and 2 leaves a committed
    // work order whose event never exists. Kept only to demonstrate the failure.
    // ---------------------------------------------------------------------------------------
    private async Task<CreateWorkOrderResult> CreateNaiveAsync(WorkOrderCreated evt, InjectedFault fault, CancellationToken ct)
    {
        await using (var conn = await db.OpenConnectionAsync(ct))
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            await InsertWorkOrderAsync(conn, tx, evt, WriteMode.Naive, ct);
            await FailBeforeCommitIfRequestedAsync(tx, evt, fault, ct);
            await tx.CommitAsync(ct);
        }
        logger.LogInformation("phase={phase} Work order committed; event is NOT yet published (separate step)", "db_committed");

        CrashAfterCommitIfRequested(fault);

        var outgoing = new OutgoingEvent(evt.EventId, WorkOrderCreated.EventType, evt.WorkOrderId, evt.OperationId, evt.ToJson());
        logger.LogInformation("phase={phase} Publishing WorkOrderCreated", "publish_started");
        try
        {
            await broker.PublishAsync(outgoing, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "phase={phase} Publish failed AFTER commit: this work order has no event", "publish_failed");
            throw new EventNotPublishedException(evt.WorkOrderId, evt.EventId, ex);
        }
        logger.LogInformation("phase={phase} Broker confirmed WorkOrderCreated", "publish_confirmed");
        return new CreateWorkOrderResult(WriteMode.Naive, evt.WorkOrderId, evt.EventId, evt.OperationId, "published");
    }

    // ---------------------------------------------------------------------------------------
    // MITIGATION (transactional outbox). The work order and the event row commit atomically.
    // The request does no broker I/O at all; OutboxPublisher relays pending rows later.
    // ---------------------------------------------------------------------------------------
    private async Task<CreateWorkOrderResult> CreateWithOutboxAsync(WorkOrderCreated evt, InjectedFault fault, CancellationToken ct)
    {
        await using (var conn = await db.OpenConnectionAsync(ct))
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            await InsertWorkOrderAsync(conn, tx, evt, WriteMode.Outbox, ct);
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
            await FailBeforeCommitIfRequestedAsync(tx, evt, fault, ct);
            await tx.CommitAsync(ct);
        }
        logger.LogInformation("phase={phase} outbox_status={outbox_status} Work order and outbox event committed atomically",
            "db_committed", "pending");

        // Same kill point as the baseline. Here nothing is lost: the publish intent is already durable.
        CrashAfterCommitIfRequested(fault);

        return new CreateWorkOrderResult(WriteMode.Outbox, evt.WorkOrderId, evt.EventId, evt.OperationId, "pending");
    }

    private static async Task InsertWorkOrderAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, WorkOrderCreated evt, WriteMode mode, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO work_orders (id, title, operation_id, write_mode, created_at)
            VALUES (@id, @title, @operation_id, @write_mode, @created_at)
            """, conn, tx);
        cmd.Parameters.AddWithValue("id", evt.WorkOrderId);
        cmd.Parameters.AddWithValue("title", evt.Title);
        cmd.Parameters.AddWithValue("operation_id", evt.OperationId);
        cmd.Parameters.AddWithValue("write_mode", mode.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue("created_at", evt.OccurredAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task FailBeforeCommitIfRequestedAsync(NpgsqlTransaction tx, WorkOrderCreated evt, InjectedFault fault, CancellationToken ct)
    {
        if (fault != InjectedFault.FailBeforeCommit) return;
        logger.LogWarning("phase={phase} fault={fault} Failing before COMMIT", "fault_injected", "fail-before-commit");
        await tx.RollbackAsync(ct);
        logger.LogWarning("phase={phase} Transaction rolled back: no work order, no outbox row", "db_rolled_back");
        throw new InjectedFaultException(evt.WorkOrderId, evt.EventId);
    }

    private void CrashAfterCommitIfRequested(InjectedFault fault)
    {
        if (fault != InjectedFault.CrashAfterCommit) return;
        logger.LogWarning("phase={phase} fault={fault} COMMIT done; terminating before any further step", "fault_injected", "crash-after-commit");
        terminator.TerminateNow("crash-after-commit");
    }
}
