using Lab.Shared;
using Npgsql;

namespace WorkOrders;

/// <summary>The outbox worker's crash point. Armed with POST /faults/crash-after-publish-confirm.</summary>
public sealed class PublisherFaults
{
    public const string CrashAfterPublishConfirm = "crash-after-publish-confirm";

    /// <summary>SIGKILL after the broker confirmed the message, before the outbox row is marked published.</summary>
    public ArmedFault AfterConfirmBeforeMark { get; } = new(CrashAfterPublishConfirm);
}

/// <summary>
/// Relays pending outbox rows to RabbitMQ, one batch per call (E01's publisher, plus one crash point).
///
/// Claim: SELECT ... FOR UPDATE SKIP LOCKED inside a transaction. If this process dies, its
/// transaction aborts, the row locks vanish and the rows are simply pending again.
///
/// Guarantee: at-least-once publication. If the broker confirms a message and the process dies
/// before the UPDATE ... published_at commits, the row stays pending and is published AGAIN with
/// the same event_id after a restart. E02 injects exactly that crash. There is no exactly-once
/// delivery here; consumers must deduplicate.
/// </summary>
public sealed class OutboxPublisher(
    NpgsqlDataSource db,
    Broker broker,
    WorkOrdersOptions options,
    PublisherFaults faults,
    IProcessTerminator terminator,
    ILogger<OutboxPublisher> logger)
{
    private sealed record PendingRow(Guid EventId, string EventType, Guid AggregateId, string OperationId, string Payload);

    /// <returns>Number of events confirmed by the broker and marked published.</returns>
    public async Task<int> PublishPendingBatchAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var rows = new List<PendingRow>();
        await using (var select = new NpgsqlCommand(
            """
            SELECT event_id, event_type, aggregate_id, operation_id, payload::text
            FROM outbox_messages
            WHERE published_at IS NULL
            ORDER BY created_at, event_id
            LIMIT @batch
            FOR UPDATE SKIP LOCKED
            """, conn, tx))
        {
            select.Parameters.AddWithValue("batch", options.PublisherBatchSize);
            await using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                rows.Add(new PendingRow(reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2), reader.GetString(3), reader.GetString(4)));
        }

        if (rows.Count == 0)
        {
            await tx.CommitAsync(ct);
            return 0;
        }

        logger.LogInformation("phase={phase} count={count} Claimed pending outbox rows", "outbox_claimed", rows.Count);

        var confirmed = new List<Guid>(rows.Count);
        try
        {
            foreach (var row in rows)
            {
                using var scope = logger.BeginScope(new Dictionary<string, object?>
                {
                    ["event_id"] = row.EventId,
                    ["work_order_id"] = row.AggregateId,
                    ["operation_id"] = row.OperationId,
                });
                await broker.PublishAsync(new OutgoingEvent(row.EventId, row.EventType, row.AggregateId, row.OperationId, row.Payload), ct);
                logger.LogInformation("phase={phase} Broker confirmed outbox event", "publish_confirmed");
                confirmed.Add(row.EventId);

                // E02 crash point: the broker has the message, the outbox row still says pending.
                if (faults.AfterConfirmBeforeMark.TryFire(row.OperationId))
                {
                    logger.LogWarning("phase={phase} fault={fault} Broker confirmed; terminating BEFORE marking the outbox row published",
                        "fault_injected", PublisherFaults.CrashAfterPublishConfirm);
                    terminator.TerminateNow(PublisherFaults.CrashAfterPublishConfirm);
                }
            }
        }
        catch when (confirmed.Count > 0)
        {
            // Keep the progress we have; the failed row and the rest stay pending.
            await MarkPublishedAsync(conn, tx, confirmed, ct);
            await tx.CommitAsync(ct);
            throw;
        }

        await MarkPublishedAsync(conn, tx, confirmed, ct);
        await tx.CommitAsync(ct);
        return confirmed.Count;
    }

    private async Task MarkPublishedAsync(NpgsqlConnection conn, NpgsqlTransaction tx, List<Guid> eventIds, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "UPDATE outbox_messages SET published_at = now() WHERE event_id = ANY(@ids) AND published_at IS NULL", conn, tx);
        cmd.Parameters.AddWithValue("ids", eventIds.ToArray());
        var marked = await cmd.ExecuteNonQueryAsync(ct);
        logger.LogInformation("phase={phase} count={count} event_ids={event_ids} Marked outbox rows published",
            "outbox_marked_published", marked, string.Join(",", eventIds));
    }
}

/// <summary>Polls the outbox. Runs only in the outbox-worker role.</summary>
public sealed class OutboxPublisherWorker(OutboxPublisher publisher, WorkOrdersOptions options, ILogger<OutboxPublisherWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("phase={phase} poll_ms={poll_ms} Outbox publisher started",
            "publisher_started", (int)options.PublisherPollInterval.TotalMilliseconds);

        var attempt = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var published = await publisher.PublishPendingBatchAsync(stoppingToken);
                attempt = 0;
                if (published == 0) await Task.Delay(options.PublisherPollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                attempt++;
                var delay = TimeSpan.FromMilliseconds(Math.Min(
                    options.PublisherRetryBaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1),
                    options.PublisherRetryMaxDelay.TotalMilliseconds));
                logger.LogWarning(ex, "phase={phase} attempt={attempt} retry_in_ms={retry_in_ms} Outbox rows remain pending",
                    Broker.IsBrokerUnavailable(ex) ? "broker_unavailable" : "publish_failed", attempt, (int)delay.TotalMilliseconds);
                try { await Task.Delay(delay, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}
