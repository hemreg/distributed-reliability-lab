using Dependency;
using LoadGenerator;

namespace RetryResilience.Tests;

public sealed class CorrelatedFailureTests
{
    private static CorrelatedFailure Defaults => DependencyOptions.Defaults.Correlated;

    [Theory]
    [InlineData(0, CorrelatedPhase.Normal)]
    [InlineData(8_499.9, CorrelatedPhase.Normal)]
    [InlineData(8_500, CorrelatedPhase.Frozen)]
    [InlineData(9_999.9, CorrelatedPhase.Frozen)]
    [InlineData(10_000, CorrelatedPhase.Cold)]     // no outage by default
    [InlineData(14_999.9, CorrelatedPhase.Cold)]
    [InlineData(15_000, CorrelatedPhase.Recovered)]
    public void Default_timeline_freeze_8_5_restart_10_cold_until_15(double ms, CorrelatedPhase expected) =>
        Assert.Equal(expected, Defaults.PhaseAt(ms));

    [Fact]
    public void An_outage_sits_between_the_restart_and_the_cold_start()
    {
        var f = Defaults with { OutageMs = 2_000 };
        Assert.Equal(CorrelatedPhase.Outage, f.PhaseAt(10_000));
        Assert.Equal(CorrelatedPhase.Outage, f.PhaseAt(11_999.9));
        Assert.Equal(CorrelatedPhase.Cold, f.PhaseAt(12_000));
        Assert.Equal(CorrelatedPhase.Recovered, f.PhaseAt(17_000));
        Assert.Equal(17_000, f.ColdEndMs);
    }

    [Fact]
    public void Service_time_is_slower_only_during_the_cold_start()
    {
        Assert.Equal(40, Defaults.ServiceTimeAt(5_000));
        Assert.Equal(80, Defaults.ServiceTimeAt(10_000));
        Assert.Equal(80, Defaults.ServiceTimeAt(14_999));
        Assert.Equal(40, Defaults.ServiceTimeAt(15_000));
        Assert.Equal(125, DependencyOptions.Defaults.Workers * 1000.0 / Defaults.ColdServiceTimeMs);
    }

    [Fact]
    public void The_default_freeze_is_shorter_than_the_attempt_timeout_so_held_requests_fail_together() =>
        Assert.True(Defaults.FreezeMs < RetryLoop.AttemptTimeout.TotalMilliseconds);

    [Fact]
    public void Rejects_a_freeze_that_starts_before_the_run_and_negative_durations()
    {
        Assert.Throws<ArgumentException>(() => (Defaults with { FreezeMs = 10_001 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (Defaults with { OutageMs = -1 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (Defaults with { ColdMs = -1 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (Defaults with { ColdServiceTimeMs = 0 }).Validate());
    }
}

/// <summary>
/// The E03-B request path of the dependency, driven directly (no HTTP) with a short timeline:
/// freeze at 200 ms, restart at 400 ms (triggered by the test), cold start until 700 ms.
/// </summary>
public sealed class CorrelatedWorkTests
{
    private static readonly DependencyOptions Options = DependencyOptions.Defaults with
    {
        Workers = 2,
        QueueLimit = 2,
        ServiceTimeMs = 150,
        RestartMs = 400,
        FreezeMs = 200,
        OutageMs = 0,
        ColdMs = 300,
        ColdServiceTimeMs = 120,
    };

    private static async Task WaitUntil(ServerRun run, double ms)
    {
        var wait = ms - run.ElapsedMs;
        if (wait > 0) await Task.Delay(TimeSpan.FromMilliseconds(wait), TestContext.Current.CancellationToken);
    }

    private static async Task<(WorkOutcome Outcome, double DoneMs)> Send(ServerRun run, DependencyOptions options)
    {
        var outcome = await WorkProcessor.ProcessAsync(run, options, CancellationToken.None);
        return (outcome, run.ElapsedMs);
    }

    [Fact]
    public async Task Every_request_inside_at_the_restart_fails_at_that_instant_including_those_beyond_the_queue()
    {
        var run = new ServerRun("t", FailureMode.CorrelatedRestart, Options);

        // Two in service from 100 ms: their 150 ms of work ends at 250 ms, inside the freeze, so they are held.
        await WaitUntil(run, 100);
        var inService = Enumerable.Range(0, 2).Select(_ => Send(run, Options)).ToArray();

        // Five more during the freeze: more than workers + queue (4 in total), all admitted and held.
        await WaitUntil(run, 260);
        var duringFreeze = Enumerable.Range(0, 5).Select(_ => Send(run, Options)).ToArray();
        await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.Equal(7, run.InSystem);
        Assert.All(inService.Concat(duringFreeze), t => Assert.False(t.IsCompleted));

        await WaitUntil(run, 400);
        run.Restart();
        var results = await Task.WhenAll(inService.Concat(duringFreeze));

        Assert.All(results, r => Assert.Equal(WorkOutcome.Restart503, r.Outcome));
        var done = results.Select(r => r.DoneMs).ToArray();
        Assert.True(done.Min() >= run.RestartedAtMs - 0.001);
        Assert.True(done.Max() - done.Min() < 50, $"spread {done.Max() - done.Min():0.0} ms");

        var stats = run.Snapshot(Options);
        Assert.Equal(7, stats.CorrelatedTotals!.Restart503);
        Assert.Equal(7, stats.CorrelatedTotals.HeldAtRestart);
        Assert.Equal(0, stats.Totals.Ok);
        Assert.Equal(0, run.InSystem);
    }

    [Fact]
    public async Task Before_the_freeze_it_serves_normally_and_sheds_beyond_workers_plus_queue()
    {
        var run = new ServerRun("t", FailureMode.CorrelatedRestart, Options with { ServiceTimeMs = 20 });
        var requests = Enumerable.Range(0, 6).Select(_ => Send(run, Options with { ServiceTimeMs = 20 })).ToArray();
        var outcomes = (await Task.WhenAll(requests)).Select(r => r.Outcome).ToArray();

        Assert.Equal(4, outcomes.Count(o => o == WorkOutcome.Ok));
        Assert.Equal(2, outcomes.Count(o => o == WorkOutcome.Rejected503));
    }

    [Fact]
    public async Task After_the_restart_a_fresh_process_admits_workers_plus_queue_and_serves_at_cold_speed()
    {
        var run = new ServerRun("t", FailureMode.CorrelatedRestart, Options);
        await WaitUntil(run, 300);
        var held = Send(run, Options); // held by the freeze; must not occupy the new process
        await WaitUntil(run, 400);
        run.Restart();
        Assert.Equal(WorkOutcome.Restart503, (await held).Outcome);

        var started = run.ElapsedMs;
        var burst = Enumerable.Range(0, 6).Select(_ => Send(run, Options)).ToArray();
        var results = await Task.WhenAll(burst);

        Assert.Equal(4, results.Count(r => r.Outcome == WorkOutcome.Ok));
        Assert.Equal(2, results.Count(r => r.Outcome == WorkOutcome.Rejected503));
        // Cold start: 120 ms per request, two workers, four admitted: the last ones finish after about 240 ms.
        var lastOk = results.Where(r => r.Outcome == WorkOutcome.Ok).Max(r => r.DoneMs);
        Assert.InRange(lastOk - started, 230, 600);
        Assert.Equal(4, run.Snapshot(Options).CorrelatedTotals!.PeakInSystemAfterRestart);
    }

    [Fact]
    public async Task During_the_outage_every_request_is_refused_at_once()
    {
        var options = Options with { OutageMs = 200, ColdMs = 100 };
        var run = new ServerRun("t", FailureMode.CorrelatedRestart, options);
        await WaitUntil(run, 400);
        run.Restart();
        var (outcome, done) = await Send(run, options);
        Assert.Equal(WorkOutcome.Refused503, outcome);
        Assert.True(done < 600);
        Assert.Equal(1, run.Snapshot(options).CorrelatedTotals!.Refused503);

        await WaitUntil(run, 610);
        Assert.Equal(WorkOutcome.Ok, (await Send(run, options)).Outcome);
    }

    [Fact]
    public async Task The_E03_A_window_mode_is_not_affected_by_the_freeze_settings()
    {
        // Same options, mode Window: no freeze, no restart; outside the 10-20 s window every request succeeds.
        var options = Options with { ServiceTimeMs = 20 };
        var run = new ServerRun("t", FailureMode.Window, options);
        await WaitUntil(run, 250); // would be inside the freeze in E03-B
        Assert.Equal(WorkOutcome.Ok, (await Send(run, options)).Outcome);
        Assert.Same(run.Model, run.ModelAfterRestart);
        Assert.Null(run.Snapshot(options).CorrelatedTotals);
    }
}
