using System.Globalization;

namespace LoadGenerator;

/// <param name="MaxDispatchLagMs">E03-A: how late an arrival was dispatched; E03-B: how late a client woke up.</param>
public sealed record StrategyRun(
    Strategy Strategy, RunRecorder Recorder, RunStats Dependency, double MaxDispatchLagMs, double DurationSeconds)
{
    /// <summary>E03-B: offset applied to the client clock to align it with the dependency's clock (ms).</summary>
    public double ClockOffsetMs { get; init; }
}

/// <summary>Runs one strategy: warm-up, reset, open-loop arrivals, drain, dependency stats.</summary>
public static class StrategyRunner
{
    public static async Task<StrategyRun> RunAsync(
        Strategy strategy, double[] arrivalsMs, LoadOptions options, DependencyAdmin admin, TextWriter? attemptLog)
    {
        // A fresh connection pool per strategy, so no strategy inherits another's connections.
        using var handler = new InstrumentedHandler(new SocketsHttpHandler
        {
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            MaxConnectionsPerServer = int.MaxValue,
        });
        // No HttpClient.Timeout: the per-attempt 2 s timeout of the retry loop is the only one.
        using var client = new HttpClient(handler) { BaseAddress = options.DependencyUrl, Timeout = Timeout.InfiniteTimeSpan };

        // 1. Warm-up (not measured): same rate, single attempts, failure window disabled. Opens
        //    connections and JIT-compiles both sides so the measured run does not start cold.
        await admin.ResetAsync($"warmup-{strategy.Id}", failure: false);
        await WarmUpAsync(client, options.RatePerSecond, options.WarmupSeconds);

        // 2. Measured run: the dependency's clock and failure window start at this reset.
        await admin.ResetAsync(strategy.Id, failure: true);
        var recorder = new RunRecorder(arrivalsMs.Length, strategy.Id, attemptLog);
        handler.Recorder = recorder;
        recorder.Start();

        var operations = new Task[arrivalsMs.Length];
        var maxLagMs = 0.0;
        var nextProgress = 5;
        for (var i = 0; i < arrivalsMs.Length; i++)
        {
            var due = arrivalsMs[i];
            var wait = due - recorder.ElapsedMs;
            if (wait > 1) await Task.Delay(TimeSpan.FromMilliseconds(wait));
            maxLagMs = Math.Max(maxLagMs, recorder.ElapsedMs - due);

            var index = i;
            operations[i] = Task.Run(() => RunOperationAsync(client, strategy, recorder, index, due));

            if (recorder.ElapsedMs >= nextProgress * 1000)
            {
                PrintProgress(strategy, recorder, nextProgress);
                nextProgress += 5;
            }
        }
        while (nextProgress <= options.DurationSeconds)
        {
            var untilMark = nextProgress * 1000 - recorder.ElapsedMs;
            if (untilMark > 0) await Task.Delay(TimeSpan.FromMilliseconds(untilMark));
            PrintProgress(strategy, recorder, nextProgress);
            nextProgress += 5;
        }
        await Task.WhenAll(operations);
        var duration = recorder.ElapsedMs / 1000.0;

        // 3. Requests the clients gave up on may still be inside the dependency: wait for them.
        var stats = await admin.IdleStatsAsync();
        handler.Recorder = null;
        return new StrategyRun(strategy, recorder, stats, maxLagMs, duration);
    }

    /// <summary>
    /// One logical operation: the shared retry loop, the strategy's wait function and deadline, and
    /// (strategy D) the retry budget. Shared by the open-loop (E03-A) and closed-loop (E03-B) runners.
    /// </summary>
    internal static async Task RunOperationAsync(HttpClient client, Strategy strategy, RunRecorder recorder, int index, double scheduledMs)
    {
        strategy.Budget?.OnFirstAttempt();
        using var deadline = strategy.OperationDeadline is { } limit ? new CancellationTokenSource(limit) : null;
        var token = deadline?.Token ?? CancellationToken.None;
        var op = new OperationContext(index, token);
        OperationContext.Current = op;

        string outcome;
        var succeeded = false;
        try
        {
            using var response = await RetryLoop.GetWithRetryAsync(
                client, $"/work?op={index.ToString(CultureInfo.InvariantCulture)}", strategy.DelayForOperation(index), token);
            succeeded = response.IsSuccessStatusCode;
            outcome = succeeded ? "succeeded"
                : RetryLoop.IsTransient(response.StatusCode) ? $"exhausted_{(int)response.StatusCode}"
                : $"status_{(int)response.StatusCode}";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            outcome = "deadline_exceeded";
        }
        catch (RetryBudgetExhaustedException)
        {
            recorder.RetryDenied();
            outcome = "retry_budget_exhausted";
        }
        catch (OperationCanceledException)
        {
            outcome = "exhausted_timeout";
        }
        catch (HttpRequestException)
        {
            outcome = "exhausted_http_error";
        }

        var latency = recorder.ElapsedMs - scheduledMs;
        recorder.Completed(new OperationResult(index, scheduledMs, latency, succeeded, outcome, op.Attempts));
    }

    internal static async Task WarmUpAsync(HttpClient client, double ratePerSecond, int seconds)
    {
        if (seconds <= 0) return;
        var count = (int)(ratePerSecond * seconds);
        var gap = TimeSpan.FromSeconds(1.0 / ratePerSecond);
        var requests = new List<Task>(count);
        for (var i = 0; i < count; i++)
        {
            requests.Add(Task.Run(async () =>
            {
                using var timeout = new CancellationTokenSource(RetryLoop.AttemptTimeout);
                try
                {
                    using var _ = await client.GetAsync("/work?warmup=1", timeout.Token);
                }
                catch (Exception e) when (e is HttpRequestException or OperationCanceledException) { }
            }));
            await Task.Delay(gap);
        }
        await Task.WhenAll(requests);
    }

    internal static void PrintProgress(Strategy strategy, RunRecorder recorder, int untilSecond)
    {
        long Sum(IReadOnlyList<long> perSecond) => Enumerable.Range(untilSecond - 5, 5).Sum(s => perSecond[s]);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  [{strategy.Id}] t={untilSecond - 5,2}-{untilSecond,2} s  attempts sent {Sum(recorder.AttemptsPerSecond) / 5.0,6:0}/s   " +
            $"ops succeeded {Sum(recorder.SucceededPerSecond) / 5.0,5:0}/s   failed {Sum(recorder.FailedPerSecond) / 5.0,5:0}/s"));
    }
}
