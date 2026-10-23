using Inventory;

namespace Idempotency.Tests;

/// <summary>
/// The consumer's transaction, in process, against the real PostgreSQL: what one delivery does to
/// the business tables, and why the idempotent consumer's answer is decided by the primary key
/// inside the same transaction (concurrency, rollback, scope).
/// </summary>
public sealed class ConsumerTransactionTests(LabFixture lab) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await lab.ResetAsync();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static WorkOrderCreated NewEvent(string operationId) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), $"work order {operationId}", operationId, ConsumerHost.SealKit, 1, DateTimeOffset.UtcNow);

    [Fact]
    public async Task Naive_baseline_reserves_again_on_every_delivery_of_the_same_event()
    {
        var evt = NewEvent("op-naive-3x");
        var handler = lab.Handler(ConsumerMode.Naive);
        for (var i = 0; i < 3; i++)
            Assert.Equal(ProcessingOutcome.Applied, (await handler.HandleAsync(evt, TestContext.Current.CancellationToken)).Outcome);

        Assert.Equal(3L, await lab.ReservationsAsync(LabFixture.Naive, evt.EventId));
        Assert.Equal(LabFixture.InitialStock - 3, await lab.OnHandAsync(LabFixture.Naive));
        Assert.Equal(0L, await lab.ProcessedAsync(LabFixture.Naive));
    }

    [Fact]
    public async Task Idempotent_consumer_applies_an_event_once_and_skips_later_deliveries()
    {
        var evt = NewEvent("op-idem-3x");
        var handler = lab.Handler(ConsumerMode.Idempotent);
        var outcomes = new List<ProcessingOutcome>();
        for (var i = 0; i < 3; i++) outcomes.Add((await handler.HandleAsync(evt, TestContext.Current.CancellationToken)).Outcome);

        Assert.Equal([ProcessingOutcome.Applied, ProcessingOutcome.Duplicate, ProcessingOutcome.Duplicate], outcomes);
        Assert.Equal(1L, await lab.ReservationsAsync(LabFixture.Idempotent, evt.EventId));
        Assert.Equal(LabFixture.InitialStock - 1, await lab.OnHandAsync(LabFixture.Idempotent));
        Assert.Equal(1L, await lab.ProcessedAsync(LabFixture.Idempotent));
    }

    [Fact]
    public async Task Two_concurrent_deliveries_of_the_same_event_id_the_primary_key_lets_exactly_one_win()
    {
        var evt = NewEvent("op-race-2");
        var firstInside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Instance 1 holds its transaction open after writing everything, before COMMIT.
        var first = lab.Handler(ConsumerMode.Idempotent, beforeCommit: async (_, _) => { firstInside.TrySetResult(); await release.Task; });
        // Instance 2 is a separate handler with its own connection, like a second consumer process.
        var second = lab.Handler(ConsumerMode.Idempotent);

        var t1 = first.HandleAsync(evt, TestContext.Current.CancellationToken);
        Task<ProcessingResult>? t2 = null;
        bool blocked;
        try
        {
            await firstInside.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            t2 = second.HandleAsync(evt, TestContext.Current.CancellationToken);

            // Instance 2 cannot decide while instance 1's key is uncommitted: it waits on the unique index.
            await Task.Delay(500, TestContext.Current.CancellationToken);
            blocked = !t2.IsCompleted;
        }
        finally
        {
            release.TrySetResult(); // never leave instance 1's transaction (and its locks) open
        }
        var results = await Task.WhenAll(t1, t2);
        Assert.True(blocked, "the second delivery should have been blocked by the first transaction");

        Assert.Equal(ProcessingOutcome.Applied, results[0].Outcome);
        Assert.Equal(ProcessingOutcome.Duplicate, results[1].Outcome);
        Assert.Equal(1L, await lab.ReservationsAsync(LabFixture.Idempotent, evt.EventId));
        Assert.Equal(LabFixture.InitialStock - 1, await lab.OnHandAsync(LabFixture.Idempotent));
        Assert.Equal(1L, await lab.ProcessedAsync(LabFixture.Idempotent));
    }

    [Fact]
    public async Task Sixteen_concurrent_deliveries_of_the_same_event_id_create_exactly_one_reservation()
    {
        var evt = NewEvent("op-race-16");
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 16).Select(async _ =>
        {
            var handler = lab.Handler(ConsumerMode.Idempotent);
            await start.Task;
            return (await handler.HandleAsync(evt, TestContext.Current.CancellationToken)).Outcome;
        }).ToList();
        start.SetResult();
        var outcomes = await Task.WhenAll(tasks);

        Assert.Equal(1, outcomes.Count(o => o == ProcessingOutcome.Applied));
        Assert.Equal(15, outcomes.Count(o => o == ProcessingOutcome.Duplicate));
        Assert.Equal(1L, await lab.ReservationsAsync(LabFixture.Idempotent, evt.EventId));
        Assert.Equal(LabFixture.InitialStock - 1, await lab.OnHandAsync(LabFixture.Idempotent));
    }

    [Fact]
    public async Task A_rolled_back_transaction_leaves_no_processed_marker_so_the_redelivery_applies_the_effect()
    {
        var evt = NewEvent("op-rollback");
        var failing = lab.Handler(ConsumerMode.Idempotent,
            beforeCommit: (_, _) => throw new InvalidOperationException("injected failure before COMMIT"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.HandleAsync(evt, TestContext.Current.CancellationToken));

        // Nothing of the failed attempt is left: no marker, no reservation, stock untouched.
        Assert.Equal(0L, await lab.ProcessedAsync(LabFixture.Idempotent));
        Assert.Equal(0L, await lab.ReservationsAsync(LabFixture.Idempotent, evt.EventId));
        Assert.Equal(LabFixture.InitialStock, await lab.OnHandAsync(LabFixture.Idempotent));

        // The redelivery is therefore not mistaken for a duplicate.
        var handler = lab.Handler(ConsumerMode.Idempotent);
        Assert.Equal(ProcessingOutcome.Applied, (await handler.HandleAsync(evt, TestContext.Current.CancellationToken)).Outcome);
        Assert.Equal(ProcessingOutcome.Duplicate, (await handler.HandleAsync(evt, TestContext.Current.CancellationToken)).Outcome);
        Assert.Equal(1L, await lab.ReservationsAsync(LabFixture.Idempotent, evt.EventId));
        Assert.Equal(LabFixture.InitialStock - 1, await lab.OnHandAsync(LabFixture.Idempotent));
    }

    [Fact]
    public async Task Duplicate_detection_is_scoped_per_consumer()
    {
        var evt = NewEvent("op-scope");
        var other = lab.Handler(ConsumerMode.Idempotent, name: "idempotent-b");
        await other.EnsureStockAsync(ConsumerHost.SealKit, TestContext.Current.CancellationToken);
        var mine = lab.Handler(ConsumerMode.Idempotent);

        // Each consumer applies the event once for its own data, and only once.
        Assert.Equal(ProcessingOutcome.Applied, (await mine.HandleAsync(evt, TestContext.Current.CancellationToken)).Outcome);
        Assert.Equal(ProcessingOutcome.Applied, (await other.HandleAsync(evt, TestContext.Current.CancellationToken)).Outcome);
        Assert.Equal(ProcessingOutcome.Duplicate, (await mine.HandleAsync(evt, TestContext.Current.CancellationToken)).Outcome);
        Assert.Equal(ProcessingOutcome.Duplicate, (await other.HandleAsync(evt, TestContext.Current.CancellationToken)).Outcome);

        Assert.Equal(1L, await lab.ReservationsAsync(LabFixture.Idempotent, evt.EventId));
        Assert.Equal(1L, await lab.ReservationsAsync("idempotent-b", evt.EventId));
    }

    [Fact]
    public async Task Processed_messages_has_a_primary_key_on_consumer_and_event_id()
    {
        var columns = await lab.Scalar<string>(
            """
            SELECT string_agg(a.attname, ',' ORDER BY array_position(c.conkey, a.attnum))
            FROM pg_constraint c
            JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = ANY (c.conkey)
            WHERE c.conrelid = 'processed_messages'::regclass AND c.contype = 'p'
            """);
        Assert.Equal("consumer,event_id", columns);
    }
}
