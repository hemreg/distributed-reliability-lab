namespace LoadGenerator;

/// <summary>
/// E03-B think times. Each client has its own seeded Random, so client c draws the same sequence of
/// think times under every strategy (the moments differ, because operations take different times).
/// </summary>
public sealed class ThinkTime(int seed, int client, int minMs, int maxMs)
{
    private readonly Random _random = new(unchecked(seed * 1_000_033 + client * 7_919 + 1));

    /// <summary>
    /// When the client starts its first operation: uniform in [0, max) ms, so the population does not
    /// start in lockstep at t = 0 (that would be a synchronisation the experiment did not intend).
    /// </summary>
    public double InitialOffsetMs() => _random.NextDouble() * maxMs;

    /// <summary>Think time after an operation: uniform in [min, max] whole milliseconds.</summary>
    public int NextMs() => _random.Next(minMs, maxMs + 1);
}

/// <summary>
/// E03-B runner: a fixed population of clients in a closed loop. Each client runs one logical
/// operation at a time (the same retry loop and strategy as E03-A), then thinks, then starts the next.
/// </summary>
public static class ClosedLoopRunner
{
    /// <summary>Operation index = client * stride + k, so op k of client c gets the same jitter seed under C and D.</summary>
    public const int OperationsPerClientStride = 100_000;

    public static async Task<StrategyRun> RunAsync(
        Strategy strategy, CorrelatedLoadOptions b, LoadOptions options, DependencyAdmin admin, TextWriter? attemptLog)
    {
        using var handler = new InstrumentedHandler(new SocketsHttpHandler
        {
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            MaxConnectionsPerServer = int.MaxValue,
        });
        using var client = new HttpClient(handler) { BaseAddress = options.DependencyUrl, Timeout = Timeout.InfiniteTimeSpan };

        // 1. Warm-up (not measured), as in E03-A, at about the population's offered rate.
        await admin.ResetAsync($"warmup-b-{strategy.Id}", failure: false);
        await StrategyRunner.WarmUpAsync(client, b.ApproximateRatePerSecond, options.WarmupSeconds);

        // 2. Measured run: the dependency's clock and the freeze/restart timeline start at this reset.
        var reset = await admin.ResetAsync($"b-{strategy.Id}", failure: true, scenario: "b");
        var capacityHint = (int)Math.Min(1_000_000, b.Clients * (b.DurationSeconds * 1000.0 / Math.Max(100, b.ThinkMinMs) + 2));
        var recorder = new RunRecorder(capacityHint, strategy.Id, attemptLog);
        handler.Recorder = recorder;
        // Align with the dependency's clock: it read ElapsedSeconds when it answered, about half a round trip ago.
        var clockOffsetMs = reset.ElapsedSeconds * 1000 + admin.LastResetRoundTripMs / 2;
        recorder.Start(clockOffsetMs);

        var lag = new MaxLag();
        var durationMs = b.DurationSeconds * 1000.0;
        var clients = Enumerable.Range(0, b.Clients)
            .Select(c => Task.Run(() => ClientAsync(client, strategy, recorder, new ThinkTime(options.Seed, c, b.ThinkMinMs, b.ThinkMaxMs), c, durationMs, lag)))
            .ToArray();

        for (var mark = 5; mark <= b.DurationSeconds; mark += 5)
        {
            var untilMark = mark * 1000 - recorder.ElapsedMs;
            if (untilMark > 0) await Task.Delay(TimeSpan.FromMilliseconds(untilMark));
            StrategyRunner.PrintProgress(strategy, recorder, mark);
        }
        await Task.WhenAll(clients);
        var duration = recorder.ElapsedMs / 1000.0;

        // 3. Requests the clients gave up on may still be inside the dependency: wait for them.
        var stats = await admin.IdleStatsAsync();
        handler.Recorder = null;
        return new StrategyRun(strategy, recorder, stats, lag.Value, duration) { ClockOffsetMs = clockOffsetMs };
    }

    private static async Task ClientAsync(
        HttpClient http, Strategy strategy, RunRecorder recorder, ThinkTime think, int clientIndex, double durationMs, MaxLag lag)
    {
        await SleepUntilAsync(recorder, think.InitialOffsetMs(), lag);
        for (var k = 0; ; k++)
        {
            var start = recorder.ElapsedMs;
            if (start >= durationMs) return;
            await StrategyRunner.RunOperationAsync(http, strategy, recorder, clientIndex * OperationsPerClientStride + k, start);
            await SleepUntilAsync(recorder, recorder.ElapsedMs + think.NextMs(), lag);
        }
    }

    private static async Task SleepUntilAsync(RunRecorder recorder, double dueMs, MaxLag lag)
    {
        var wait = dueMs - recorder.ElapsedMs;
        if (wait > 1) await Task.Delay(TimeSpan.FromMilliseconds(wait));
        lag.Observe(recorder.ElapsedMs - dueMs);
    }

    /// <summary>How late a client woke up compared with its plan: shows whether the load generator kept up.</summary>
    private sealed class MaxLag
    {
        private readonly Lock _lock = new();
        private double _value;

        public double Value { get { lock (_lock) return _value; } }

        public void Observe(double lagMs)
        {
            lock (_lock) _value = Math.Max(_value, lagMs);
        }
    }
}
