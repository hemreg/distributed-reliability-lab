using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace WorkOrders.Tests;

/// <summary>
/// E01 Lost Event. Acceptance criteria from docs/experiments.md:
///   (a) every committed work order has a corresponding durable outbox entry;
///   (b) a restart eventually publishes pending entries when broker connectivity is restored;
///   (c) a rolled-back work order creates no committed outbox event.
/// Plus the baseline: the naive dual write loses the event when killed after COMMIT.
/// None of these tests asserts exactly-once delivery; the outbox gives at-least-once publication.
/// </summary>
public sealed class LostEventTests(LabFixture lab) : IAsyncLifetime
{
    private const int SigKillExitCode = 137; // 128 + SIGKILL(9)

    public async ValueTask InitializeAsync() => await lab.ResetAsync();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Baseline_naive_kill_after_commit_leaves_committed_work_order_without_event()
    {
        await using var app = await AppProcess.StartAsync(lab.AppEnvironment(WriteMode.Naive));

        var ok = await app.CreateWorkOrderAsync("op-naive-ok");
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        var okBody = await ok.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal("published", okBody.GetProperty("event_status").GetString());

        // The process is SIGKILLed right after COMMIT: the client sees a dropped connection.
        await Assert.ThrowsAsync<HttpRequestException>(() => app.CreateWorkOrderAsync("op-naive-crash", "crash-after-commit"));
        Assert.Equal(SigKillExitCode, await app.WaitForExitAsync(TimeSpan.FromSeconds(10)));

        var workOrders = await lab.WorkOrdersAsync();
        Assert.Equal(["op-naive-ok", "op-naive-crash"], workOrders.Select(w => w.OperationId));
        Assert.All(workOrders, w => Assert.Equal("naive", w.WriteMode));
        Assert.Empty(await lab.OutboxRowsAsync()); // the baseline keeps no record of the publish intent

        var messages = await lab.DrainQueueAsync();
        var published = Assert.Single(messages);
        Assert.Equal("op-naive-ok", published.CorrelationId);

        var crashed = workOrders.Single(w => w.OperationId == "op-naive-crash");
        Assert.DoesNotContain(messages, m => WorkOrderIdOf(m.Body) == crashed.Id); // the lost event
    }

    [Fact]
    public async Task A_every_committed_work_order_has_exactly_one_durable_outbox_entry()
    {
        var service = InProcessService(out _);
        var attempts = Enumerable.Range(1, 40).Select(async i =>
        {
            var fault = i % 4 == 0 ? InjectedFault.FailBeforeCommit : InjectedFault.None;
            try { return (Ok: true, Result: await service.CreateAsync(WriteMode.Outbox, $"wo {i}", $"op-a-{i:00}", fault, TestContext.Current.CancellationToken)); }
            catch (InjectedFaultException) { return (Ok: false, Result: (CreateWorkOrderResult?)null); }
        });
        var results = await Task.WhenAll(attempts);
        var committed = results.Where(r => r.Ok).Select(r => r.Result!).ToList();
        Assert.Equal(30, committed.Count);

        var workOrders = await lab.WorkOrdersAsync();
        var outbox = await lab.OutboxRowsAsync();

        Assert.Equal(committed.Select(c => c.WorkOrderId).Order(), workOrders.Select(w => w.Id).Order());
        foreach (var workOrder in workOrders)
        {
            var entry = Assert.Single(outbox, o => o.AggregateId == workOrder.Id);
            var expected = committed.Single(c => c.WorkOrderId == workOrder.Id);
            Assert.Equal(expected.EventId, entry.EventId);
            Assert.Equal(WorkOrderCreated.EventType, entry.EventType);
            Assert.Equal(workOrder.OperationId, entry.OperationId);
            Assert.Equal(workOrder.Id, WorkOrderIdOf(entry.Payload));
            Assert.Null(entry.PublishedAt); // no publisher ran: durable but still pending
        }
        Assert.Equal(workOrders.Count, outbox.Count); // and no orphan outbox rows
    }

    [Fact]
    public async Task A_B_outbox_entry_survives_kill_after_commit_and_is_published_after_restart_once_broker_is_reachable()
    {
        // Phase 1: broker unreachable, publisher running and failing. Writes still succeed.
        await using (var app = await AppProcess.StartAsync(lab.AppEnvironment(WriteMode.Outbox, rabbitMqUri: lab.UnreachableRabbitMqUri)))
        {
            foreach (var op in new[] { "op-b-1", "op-b-2" })
            {
                var response = await app.CreateWorkOrderAsync(op);
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                var body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
                Assert.Equal("pending", body.GetProperty("event_status").GetString());
            }

            // The publisher has picked the pending rows up and failed to reach the broker.
            await app.WaitForLogAsync("\"phase\":\"broker_unavailable\"", TimeSpan.FromSeconds(15));

            await Assert.ThrowsAsync<HttpRequestException>(() => app.CreateWorkOrderAsync("op-b-crash", "crash-after-commit"));
            Assert.Equal(SigKillExitCode, await app.WaitForExitAsync(TimeSpan.FromSeconds(10)));
        }

        // (a) Durable: every committed work order, including the one whose process was killed, has a pending entry.
        var workOrders = await lab.WorkOrdersAsync();
        var pending = await lab.OutboxRowsAsync();
        Assert.Equal(["op-b-1", "op-b-2", "op-b-crash"], workOrders.Select(w => w.OperationId));
        Assert.Equal(workOrders.Select(w => w.Id), pending.Select(o => o.AggregateId));
        Assert.All(pending, o => Assert.Null(o.PublishedAt));
        Assert.Equal(0u, await lab.QueueMessageCountAsync());

        // Phase 2 (b): restart as a fresh process with the broker reachable.
        await using (var app = await AppProcess.StartAsync(lab.AppEnvironment(WriteMode.Outbox)))
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (await lab.Scalar<long>("SELECT count(*) FROM outbox_messages WHERE published_at IS NULL") > 0)
            {
                Assert.True(DateTime.UtcNow < deadline, "pending outbox rows were not published within 30s");
                await Task.Delay(200, TestContext.Current.CancellationToken);
            }
        }

        var after = await lab.OutboxRowsAsync();
        Assert.All(after, o => Assert.NotNull(o.PublishedAt));

        var messages = await lab.DrainQueueAsync();
        // At-least-once: each pending event must arrive. (A duplicate would not fail this test; E02 covers that case.)
        Assert.Equal(pending.Select(o => o.EventId.ToString()).Order(), messages.Select(m => m.MessageId!).Distinct().Order());
        Assert.All(messages, m => Assert.Equal(WorkOrderCreated.EventType, m.Type));
        foreach (var row in pending)
            Assert.Contains(messages, m => m.MessageId == row.EventId.ToString() && WorkOrderIdOf(m.Body) == row.AggregateId);
    }

    [Fact]
    public async Task C_rolled_back_work_order_creates_no_outbox_event()
    {
        var service = InProcessService(out var publisher);

        var ex = await Assert.ThrowsAsync<InjectedFaultException>(() =>
            service.CreateAsync(WriteMode.Outbox, "will roll back", "op-c-rollback", InjectedFault.FailBeforeCommit, TestContext.Current.CancellationToken));

        Assert.Equal(0L, await lab.Scalar<long>("SELECT count(*) FROM work_orders WHERE id = @id OR operation_id = 'op-c-rollback'",
            ("id", ex.AttemptedWorkOrderId)));
        Assert.Equal(0L, await lab.Scalar<long>(
            "SELECT count(*) FROM outbox_messages WHERE event_id = @event_id OR aggregate_id = @id OR operation_id = 'op-c-rollback'",
            ("event_id", ex.AttemptedEventId), ("id", ex.AttemptedWorkOrderId)));

        // A publisher pass finds nothing to publish, so nothing reaches the broker.
        Assert.Equal(0, await publisher.PublishPendingBatchAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0u, await lab.QueueMessageCountAsync());
    }

    [Fact]
    public async Task Outbox_rows_are_immutable_except_a_single_published_at_transition()
    {
        var service = InProcessService(out _);
        var created = await service.CreateAsync(WriteMode.Outbox, "immutable", "op-immutable", InjectedFault.None, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<PostgresException>(() =>
            lab.Execute($"UPDATE outbox_messages SET payload = '{{}}' WHERE event_id = '{created.EventId}'"));

        Assert.Equal(1, await lab.Execute($"UPDATE outbox_messages SET published_at = now() WHERE event_id = '{created.EventId}'"));
        await Assert.ThrowsAsync<PostgresException>(() =>
            lab.Execute($"UPDATE outbox_messages SET published_at = now() + interval '1 second' WHERE event_id = '{created.EventId}'"));
    }

    private WorkOrderService InProcessService(out OutboxPublisher publisher)
    {
        var options = lab.Options(WriteMode.Outbox);
        var broker = new Broker(options, NullLogger<Broker>.Instance);
        publisher = new OutboxPublisher(lab.Db, broker, options, NullLogger<OutboxPublisher>.Instance);
        return new WorkOrderService(lab.Db, broker, new NoCrashExpected(), NullLogger<WorkOrderService>.Instance);
    }

    private static Guid WorkOrderIdOf(string payloadJson) =>
        JsonDocument.Parse(payloadJson).RootElement.GetProperty("work_order_id").GetGuid();

    /// <summary>In-process tests never inject a crash; real kills are exercised through AppProcess.</summary>
    private sealed class NoCrashExpected : IProcessTerminator
    {
        public void TerminateNow(string reason) => throw new InvalidOperationException($"Unexpected crash injection: {reason}");
    }
}
