using Inventory;
using WorkOrders;

namespace Idempotency.Tests;

/// <summary>
/// E02 Duplicate Delivery, end to end. The outbox worker and the two inventory consumers run as
/// separate OS processes; every injected crash is a real SIGKILL and every restart a fresh process.
///   C1 the same event_id is published twice after a crash between broker confirm and marking;
///   C2 the naive consumer reserves the part twice for that event_id (stock down by 2);
///   C3 the idempotent consumer reserves it once (stock down by 1);
///   C4 a consumer crash after COMMIT, before ack, is redelivered (redelivered=true): the naive
///      consumer reserves again, the idempotent one does not;
///   C5 restarting the worker and both consumers creates no new effects.
/// </summary>
public sealed class DuplicateDeliveryTests(LabFixture lab) : IAsyncLifetime
{
    private const int SigKillExitCode = 137; // 128 + SIGKILL(9)
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(15);

    public async ValueTask InitializeAsync() => await lab.ResetAsync();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task C1_crash_between_broker_confirm_and_marking_publishes_the_same_event_id_twice()
    {
        CreateWorkOrderResult created;
        await using (var worker = await AppProcess.StartWorkerAsync(lab.WorkerEnvironment()))
        {
            await worker.ArmAsync(PublisherFaults.CrashAfterPublishConfirm, "op-c1-dup");
            created = await lab.WorkOrderService().CreateAsync("C1", "op-c1-dup", TestContext.Current.CancellationToken);
            Assert.Equal(SigKillExitCode, await worker.WaitForExitAsync(ExitTimeout));
            Assert.Equal(1, worker.CountPhase("publish_confirmed", created.EventId));
            Assert.Equal(0, worker.CountPhase("outbox_marked_published", created.EventId));
        }

        // The broker accepted one copy; the outbox still says pending.
        Assert.Equal(1L, await lab.PendingOutboxRowsAsync());
        Assert.Equal(1u, await lab.QueueMessageCountAsync(LabFixture.AuditQueue));

        await using (var restarted = await AppProcess.StartWorkerAsync(lab.WorkerEnvironment()))
        {
            await LabFixture.WaitUntilAsync(async () => await lab.PendingOutboxRowsAsync() == 0, "outbox row marked published after restart");
            Assert.Equal(1, restarted.CountPhase("publish_confirmed", created.EventId));
        }

        var copies = await lab.DrainQueueAsync(LabFixture.AuditQueue);
        Assert.Equal(2, copies.Count);
        Assert.All(copies, m => Assert.Equal(created.EventId.ToString(), m.MessageId));
        Assert.All(copies, m => Assert.Equal("op-c1-dup", m.CorrelationId));
        // Two separate publishes, not a redelivery: the broker cannot tell they are the same event.
        Assert.All(copies, m => Assert.False(m.Redelivered));
    }

    [Fact]
    public async Task C2_C3_duplicate_publication_naive_consumer_reserves_twice_idempotent_consumer_once()
    {
        await using var naive = await AppProcess.StartConsumerAsync("naive", lab.ConsumerEnvironment(ConsumerMode.Naive));
        await using var idempotent = await AppProcess.StartConsumerAsync("idempotent", lab.ConsumerEnvironment(ConsumerMode.Idempotent));

        CreateWorkOrderResult created;
        await using (var worker = await AppProcess.StartWorkerAsync(lab.WorkerEnvironment()))
        {
            await worker.ArmAsync(PublisherFaults.CrashAfterPublishConfirm, "op-c23-dup");
            created = await lab.WorkOrderService().CreateAsync("C2/C3", "op-c23-dup", TestContext.Current.CancellationToken);
            Assert.Equal(SigKillExitCode, await worker.WaitForExitAsync(ExitTimeout));
        }
        await using (var restarted = await AppProcess.StartWorkerAsync(lab.WorkerEnvironment()))
            await LabFixture.WaitUntilAsync(async () => await lab.PendingOutboxRowsAsync() == 0, "outbox row marked published");

        await WaitUntilAckedAsync(naive, created.EventId, 2);
        await WaitUntilAckedAsync(idempotent, created.EventId, 2);

        // Both consumers saw two first-time deliveries (no redelivered flag) of one event_id.
        Assert.Equal([false, false], await lab.DeliveriesAsync(LabFixture.Naive, created.EventId));
        Assert.Equal([false, false], await lab.DeliveriesAsync(LabFixture.Idempotent, created.EventId));

        // C2: the naive baseline reserved twice: stock is down by 2 for one work order.
        Assert.Equal(2L, await lab.ReservationsAsync(LabFixture.Naive, created.EventId));
        Assert.Equal(LabFixture.InitialStock - 2, await lab.OnHandAsync(LabFixture.Naive));

        // C3: the idempotent consumer reserved once; the second delivery was recognised by the PK.
        Assert.Equal(1L, await lab.ReservationsAsync(LabFixture.Idempotent, created.EventId));
        Assert.Equal(LabFixture.InitialStock - 1, await lab.OnHandAsync(LabFixture.Idempotent));
        Assert.Equal(1L, await lab.ProcessedAsync(LabFixture.Idempotent));
        Assert.Equal(1, idempotent.CountPhase("duplicate_skipped", created.EventId));
        Assert.Equal(0, naive.CountPhase("duplicate_skipped", created.EventId));
    }

    [Fact]
    public async Task C4_consumer_crash_after_commit_before_ack_is_redelivered_naive_reserves_again_idempotent_does_not()
    {
        CreateWorkOrderResult created;
        await using (var naive = await AppProcess.StartConsumerAsync("naive", lab.ConsumerEnvironment(ConsumerMode.Naive)))
        await using (var idempotent = await AppProcess.StartConsumerAsync("idempotent", lab.ConsumerEnvironment(ConsumerMode.Idempotent)))
        {
            await naive.ArmAsync(ConsumerFaults.CrashAfterCommitBeforeAck, "op-c4-ack");
            await idempotent.ArmAsync(ConsumerFaults.CrashAfterCommitBeforeAck, "op-c4-ack");

            created = await lab.WorkOrderService().CreateAsync("C4", "op-c4-ack", TestContext.Current.CancellationToken);
            await using var worker = await AppProcess.StartWorkerAsync(lab.WorkerEnvironment());
            Assert.Equal(SigKillExitCode, await naive.WaitForExitAsync(ExitTimeout));
            Assert.Equal(SigKillExitCode, await idempotent.WaitForExitAsync(ExitTimeout));
            Assert.Equal(0, naive.CountPhase("acked", created.EventId));
            Assert.Equal(0, idempotent.CountPhase("acked", created.EventId));
            await LabFixture.WaitUntilAsync(async () => await lab.PendingOutboxRowsAsync() == 0, "published once");
        }

        // Committed before the crash, never acked: the broker holds the message again.
        foreach (var consumer in new[] { LabFixture.Naive, LabFixture.Idempotent })
        {
            Assert.Equal(1L, await lab.ReservationsAsync(consumer, created.EventId));
            await LabFixture.WaitUntilAsync(async () => await lab.QueueMessageCountAsync(LabFixture.QueueOf(consumer)) == 1,
                $"{consumer}: unacked message back in the queue");
        }
        Assert.Equal(1u, await lab.QueueMessageCountAsync(LabFixture.AuditQueue)); // published exactly once

        await using (var naive = await AppProcess.StartConsumerAsync("naive", lab.ConsumerEnvironment(ConsumerMode.Naive)))
        await using (var idempotent = await AppProcess.StartConsumerAsync("idempotent", lab.ConsumerEnvironment(ConsumerMode.Idempotent)))
        {
            await WaitUntilAckedAsync(naive, created.EventId, 1);
            await WaitUntilAckedAsync(idempotent, created.EventId, 1);
            Assert.Equal(1, idempotent.CountPhase("duplicate_skipped", created.EventId));
        }

        // The second delivery is the broker's redelivery of the same message.
        Assert.Equal([false, true], await lab.DeliveriesAsync(LabFixture.Naive, created.EventId));
        Assert.Equal([false, true], await lab.DeliveriesAsync(LabFixture.Idempotent, created.EventId));

        Assert.Equal(2L, await lab.ReservationsAsync(LabFixture.Naive, created.EventId));
        Assert.Equal(LabFixture.InitialStock - 2, await lab.OnHandAsync(LabFixture.Naive));
        Assert.Equal(1L, await lab.ReservationsAsync(LabFixture.Idempotent, created.EventId));
        Assert.Equal(LabFixture.InitialStock - 1, await lab.OnHandAsync(LabFixture.Idempotent));
    }

    [Fact]
    public async Task C5_restarting_worker_and_consumers_creates_no_new_effects()
    {
        var service = lab.WorkOrderService();
        var events = new List<Guid>();
        for (var i = 1; i <= 3; i++) events.Add((await service.CreateAsync($"C5 {i}", $"op-c5-{i}", TestContext.Current.CancellationToken)).EventId);

        var worker = await AppProcess.StartWorkerAsync(lab.WorkerEnvironment());
        var naive = await AppProcess.StartConsumerAsync("naive", lab.ConsumerEnvironment(ConsumerMode.Naive));
        var idempotent = await AppProcess.StartConsumerAsync("idempotent", lab.ConsumerEnvironment(ConsumerMode.Idempotent));
        try
        {
            await LabFixture.WaitUntilAsync(async () => await lab.PendingOutboxRowsAsync() == 0, "all published");
            await lab.PublishCopyAsync(events[1]); // one broker-level duplicate of event 2
            foreach (var (id, copies) in new[] { (events[0], 1), (events[1], 2), (events[2], 1) })
            {
                await WaitUntilAckedAsync(naive, id, copies);
                await WaitUntilAckedAsync(idempotent, id, copies);
            }
            var before = await SnapshotAsync();
            Assert.Equal(new Snapshot(4, 3, 3, LabFixture.InitialStock - 4, LabFixture.InitialStock - 3, 8), before);

            // Restart all three (graceful stop, then fresh processes), then give them time to do anything they would do.
            await worker.StopGracefullyAsync();
            await naive.StopGracefullyAsync();
            await idempotent.StopGracefullyAsync();
            await worker.DisposeAsync(); await naive.DisposeAsync(); await idempotent.DisposeAsync();

            worker = await AppProcess.StartWorkerAsync(lab.WorkerEnvironment());
            naive = await AppProcess.StartConsumerAsync("naive", lab.ConsumerEnvironment(ConsumerMode.Naive));
            idempotent = await AppProcess.StartConsumerAsync("idempotent", lab.ConsumerEnvironment(ConsumerMode.Idempotent));
            await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            Assert.Equal(before, await SnapshotAsync());
            Assert.Equal(0L, await lab.PendingOutboxRowsAsync());
            Assert.Equal(0u, await lab.QueueMessageCountAsync(LabFixture.QueueOf(LabFixture.Naive)));
            Assert.Equal(0u, await lab.QueueMessageCountAsync(LabFixture.QueueOf(LabFixture.Idempotent)));
        }
        finally
        {
            await worker.DisposeAsync(); await naive.DisposeAsync(); await idempotent.DisposeAsync();
        }
    }

    [Fact]
    public async Task Publisher_confirm_does_not_mean_a_consumer_processed_the_event()
    {
        // No consumer process is running; their queues exist (declared by the fixture).
        var created = await lab.WorkOrderService().CreateAsync("confirm only", "op-confirm-only", TestContext.Current.CancellationToken);
        await using (var worker = await AppProcess.StartWorkerAsync(lab.WorkerEnvironment()))
        {
            await LabFixture.WaitUntilAsync(async () => await lab.PendingOutboxRowsAsync() == 0, "confirmed and marked published");
            Assert.Equal(1, worker.CountPhase("publish_confirmed", created.EventId));
        }

        Assert.True(await lab.Scalar<bool>("SELECT published_at IS NOT NULL FROM outbox_messages WHERE event_id = @e", ("e", created.EventId)));
        Assert.Equal(1u, await lab.QueueMessageCountAsync(LabFixture.QueueOf(LabFixture.Naive)));
        Assert.Equal(1u, await lab.QueueMessageCountAsync(LabFixture.QueueOf(LabFixture.Idempotent)));
        Assert.Equal(0L, await lab.ReservationsAsync(LabFixture.Naive));
        Assert.Equal(0L, await lab.ReservationsAsync(LabFixture.Idempotent));
        Assert.Equal(LabFixture.InitialStock, await lab.OnHandAsync(LabFixture.Idempotent));
    }

    private sealed record Snapshot(long NaiveReservations, long IdempotentReservations, long Processed, int NaiveOnHand, int IdempotentOnHand, long Deliveries);

    private async Task<Snapshot> SnapshotAsync() => new(
        await lab.ReservationsAsync(LabFixture.Naive),
        await lab.ReservationsAsync(LabFixture.Idempotent),
        await lab.ProcessedAsync(LabFixture.Idempotent),
        await lab.OnHandAsync(LabFixture.Naive),
        await lab.OnHandAsync(LabFixture.Idempotent),
        await lab.Scalar<long>("SELECT count(*) FROM consumer_deliveries"));

    /// <summary>Waits until the consumer process logged `acked` for the event this many times.</summary>
    private static Task WaitUntilAckedAsync(AppProcess consumer, Guid eventId, int times) =>
        LabFixture.WaitUntilAsync(() => Task.FromResult(consumer.CountPhase("acked", eventId) >= times),
            $"{times} ack(s) for {eventId}");
}
