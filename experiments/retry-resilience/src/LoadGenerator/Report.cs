using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace LoadGenerator;

/// <summary>E03-A report: summary.json, timeseries.json (schema v1, unchanged) and the console table.</summary>
public static class Report
{
    public const string SummarySchema = "drlab-e03/summary/v1";
    public const string TimeseriesSchema = "drlab-e03/timeseries/v1";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static StrategySummary Summarize(StrategyRun run, int durationSeconds)
    {
        var results = run.Recorder.Results.ToArray();
        var cfg = run.Dependency.Config;
        var dep = run.Dependency.Totals;
        var ops = results.Length;
        var succeeded = results.Count(r => r.Succeeded);

        static LatencySummary Latency(IEnumerable<OperationResult> rs)
        {
            var sorted = rs.Select(r => r.LatencyMs).Order().ToArray();
            double? Round(double? v) => v is { } x ? Math.Round(x, 1) : null;
            return new LatencySummary(sorted.Length,
                Round(Metrics.Percentile(sorted, 50)), Round(Metrics.Percentile(sorted, 95)),
                Round(Metrics.Percentile(sorted, 99)), Round(sorted.Length > 0 ? sorted[^1] : null));
        }

        PhaseSummary Phase(double fromS, double toS)
        {
            var inPhase = results.Where(r => r.ScheduledMs >= fromS * 1000 && r.ScheduledMs < toS * 1000).ToArray();
            var ok = inPhase.Count(r => r.Succeeded);
            return new PhaseSummary(fromS, toS, inPhase.Length, ok, inPhase.Length - ok,
                inPhase.Length == 0 ? null : Math.Round(100.0 * ok / inPhase.Length, 2),
                inPhase.Length == 0 ? null : Math.Round((double)inPhase.Sum(r => r.Attempts) / inPhase.Length, 3));
        }

        var recovery = Metrics.RecoveryAfterWindow(
            run.Recorder.SucceededPerSecond, run.Recorder.FailedPerSecond, cfg.FailureEndSeconds, durationSeconds - 1);

        var responses = run.Recorder.Responses.OrderBy(kv => kv.Key)
            .ToDictionary(kv => kv.Key.ToString(CultureInfo.InvariantCulture), kv => kv.Value);

        return new StrategySummary(
            Id: run.Strategy.Id,
            Name: run.Strategy.Name,
            Waiting: run.Strategy.Waiting,
            UnsafeBaseline: run.Strategy.UnsafeBaseline,
            OperationDeadlineMs: run.Strategy.OperationDeadline?.TotalMilliseconds,
            Operations: new OperationTotals(
                Started: ops,
                Succeeded: succeeded,
                Failed: ops - succeeded,
                SuccessPercent: Math.Round(100.0 * succeeded / ops, 2),
                FailedBy: results.Where(r => !r.Succeeded).GroupBy(r => r.Outcome).OrderBy(g => g.Key)
                    .ToDictionary(g => g.Key, g => (long)g.Count()),
                AttemptsHistogram: results.GroupBy(r => r.Attempts).OrderBy(g => g.Key)
                    .ToDictionary(g => g.Key.ToString(CultureInfo.InvariantCulture), g => (long)g.Count())),
            Attempts: new AttemptTotals(
                SentByClient: run.Recorder.AttemptsSent,
                ReceivedByDependency: dep.Received,
                AmplificationClient: Math.Round((double)run.Recorder.AttemptsSent / ops, 3),
                AmplificationDependency: Math.Round((double)dep.Received / ops, 3),
                ResponsesByStatus: responses,
                Timeouts: run.Recorder.Timeouts,
                CutByDeadline: run.Recorder.DeadlineCut,
                HttpErrors: run.Recorder.HttpErrors),
            Dependency: dep,
            LatencyMs: new LatencyByOutcome(
                All: Latency(results),
                Succeeded: Latency(results.Where(r => r.Succeeded)),
                Failed: Latency(results.Where(r => !r.Succeeded))),
            ByArrivalPhase: new PhaseTable(
                BeforeWindow: Phase(0, cfg.FailureStartSeconds),
                InWindow: Phase(cfg.FailureStartSeconds, cfg.FailureEndSeconds),
                AfterWindow: Phase(cfg.FailureEndSeconds, durationSeconds)),
            SuccessPercentByArrivalSecond: Enumerable.Range(0, durationSeconds).Select(second => Phase(second, second + 1).SuccessPercent).ToArray(),
            Recovery: recovery,
            RunSeconds: Math.Round(run.DurationSeconds, 2),
            MaxDispatchLagMs: Math.Round(run.MaxDispatchLagMs, 2));
    }

    public static object Summary(LoadOptions options, DependencyConfig cfg, int operations, IReadOnlyList<StrategySummary> strategies, DateTimeOffset startedAt) => new
    {
        schema = SummarySchema,
        experiment = "E03 Retry Storm",
        startedAtUtc = startedAt,
        finishedAtUtc = DateTimeOffset.UtcNow,
        versions = new
        {
            runtime = RuntimeInformation.FrameworkDescription,
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.OSArchitecture.ToString(),
            images = Environment.GetEnvironmentVariable("E03_IMAGES"),
        },
        environment = new { processorCount = Environment.ProcessorCount },
        parameters = new
        {
            load = new
            {
                arrivals = "open-loop Poisson (exponential gaps), same schedule for every strategy",
                ratePerSecond = options.RatePerSecond,
                durationSeconds = options.DurationSeconds,
                operationsPerStrategy = operations,
                seed = options.Seed,
                warmupSeconds = options.WarmupSeconds,
                order = options.StrategyIds,
            },
            dependency = cfg,
            client = new
            {
                maxAttempts = RetryLoop.MaxAttempts,
                attemptTimeoutMs = RetryLoop.AttemptTimeout.TotalMilliseconds,
                retryOn = new[] { "408", "429", "502", "503", "504", "attempt timeout", "HttpRequestException" },
                strategyCOperationDeadlineMs = options.CDeadlineMs,
            },
            recovery = new
            {
                definition = "first second s >= failure end where the per-second success ratio of completed logical operations "
                    + $">= {Metrics.RecoveryThreshold:0.00} for {Metrics.RecoverySustainSeconds} consecutive seconds; recovery = s - failure end",
                threshold = Metrics.RecoveryThreshold,
                sustainSeconds = Metrics.RecoverySustainSeconds,
            },
        },
        strategies,
    };

    public static object Timeseries(DependencyConfig cfg, int durationSeconds, IReadOnlyList<StrategyRun> runs) => new
    {
        schema = TimeseriesSchema,
        bucketSeconds = 1,
        failureWindow = new { startS = cfg.FailureStartSeconds, endS = cfg.FailureEndSeconds },
        arrivalsEndS = durationSeconds,
        fields = new Dictionary<string, string>
        {
            ["t"] = "second since the run started; the bucket covers [t, t+1)",
            ["dependencyRequests"] = "requests received by the dependency in this second (measured at the dependency, admitted + rejected)",
            ["succeeded"] = "logical operations that completed successfully in this second (client side)",
            ["failed"] = "logical operations that completed with a failure in this second (client side)",
            ["inFlight"] = "peak number of requests inside the dependency (in service + queued) in this second",
            ["started"] = "logical operations scheduled to start in this second (the offered load; identical for every strategy)",
            ["attemptsSent"] = "attempts the client sent in this second (cross-check for dependencyRequests)",
            ["rejected503"] = "requests the dependency rejected at once because workers and queue were full",
            ["injected503"] = "injected transient 503s completed in this second (failure window only)",
            ["queued"] = "peak number of requests waiting in the dependency's queue in this second",
        },
        strategies = runs.Select(run =>
        {
            var byT = run.Dependency.Seconds.ToDictionary(s => s.T);
            var started = new long[RunRecorder.MaxSeconds];
            foreach (var r in run.Recorder.Results) if (r is not null) started[RunRecorder.Bucket(r.ScheduledMs)]++;
            var lastClient = LastNonZero(run.Recorder.AttemptsPerSecond, run.Recorder.SucceededPerSecond, run.Recorder.FailedPerSecond);
            var last = Math.Max(lastClient, run.Dependency.Seconds.Count - 1);
            return new
            {
                id = run.Strategy.Id,
                name = run.Strategy.Name,
                points = Enumerable.Range(0, last + 1).Select(t =>
                {
                    var d = byT.GetValueOrDefault(t);
                    return new TimeseriesPoint(t, d?.Received ?? 0, run.Recorder.SucceededPerSecond[t], run.Recorder.FailedPerSecond[t],
                        d?.PeakInFlight ?? 0, started[t], run.Recorder.AttemptsPerSecond[t], d?.Rejected503 ?? 0, d?.Injected503 ?? 0, d?.PeakQueued ?? 0);
                }).ToArray(),
            };
        }).ToArray(),
    };

    private static int LastNonZero(params IReadOnlyList<long>[] series)
    {
        var last = 0;
        foreach (var s in series)
            for (var i = s.Count - 1; i > last; i--)
                if (s[i] != 0) { last = i; break; }
        return last;
    }

    public static Task WriteJsonAsync(string path, object value) => WriteJsonAsync(path, value, Json);

    public static async Task WriteJsonAsync(string path, object value, JsonSerializerOptions json)
    {
        var temp = path + ".tmp";
        await using (var stream = File.Create(temp))
            await JsonSerializer.SerializeAsync(stream, value, json);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>The console summary (the article's Figure 2).</summary>
    public static string Table(LoadOptions options, DependencyConfig cfg, int operations, IReadOnlyList<StrategySummary> rows)
    {
        var ic = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        const int width = 100;
        var rule = new string('=', width);
        sb.AppendLine(rule);
        sb.AppendLine("E03 Retry Storm: same workload, same failure window, three client strategies");
        sb.AppendLine(rule);
        sb.AppendLine(string.Create(ic,
            $"{"Strategy",-30}{"Requests",9}{"Ampl.",8}{"Success",9}{"in window",11}{"p95",9}{"p99",9}{"Peak rps",9}{"Recovery",9}"));
        sb.AppendLine(new string('-', width));
        foreach (var r in rows)
        {
            var recovery = r.Recovery.Recovered
                ? $"{r.Recovery.Seconds} s"
                : $">{options.DurationSeconds - cfg.FailureEndSeconds - Metrics.RecoverySustainSeconds} s";
            sb.AppendLine(string.Create(ic,
                $"{Label(r),-30}{r.Attempts.ReceivedByDependency,9}{r.Attempts.AmplificationDependency,7:0.00}x" +
                $"{r.Operations.SuccessPercent,8:0.0}%{r.ByArrivalPhase.InWindow.SuccessPercent,10:0.0}%" +
                $"{Ms(r.LatencyMs.All.P95),9}{Ms(r.LatencyMs.All.P99),9}{r.Dependency.PeakReceivedPerSecond,9}{recovery,9}"));
        }
        sb.AppendLine(new string('-', width));
        sb.AppendLine(string.Create(ic,
            $"Same {operations} logical ops per strategy (Poisson {options.RatePerSecond:0.#}/s, {options.DurationSeconds} s, seed {options.Seed}). " +
            $"Dependency {cfg.CapacityPerSecond:0} req/s ({cfg.Workers} x {cfg.ServiceTimeMs} ms), queue {cfg.QueueLimit}, then 503."));
        sb.AppendLine(string.Create(ic,
            $"Failure {cfg.FailureStartSeconds}-{cfg.FailureEndSeconds} s: {cfg.FailureCapacityPerSecond:0} req/s ({cfg.FailureServiceTimeMs} ms), " +
            $"{cfg.FailureErrorRate * 100:0}% injected 503. All: {RetryLoop.MaxAttempts} attempts max, {RetryLoop.AttemptTimeout.TotalSeconds:0} s each; " +
            $"C also {options.CDeadlineMs / 1000.0:0.#} s per op."));
        sb.AppendLine("Requests: received by the dependency. Ampl.: requests per logical op. Success: ops answered 200 (in window: ops");
        sb.AppendLine(string.Create(ic,
            $"started during the failure). p95/p99: end-to-end latency, all ops. Recovery: s after the window until per-second success >= {Metrics.RecoveryThreshold * 100:0}% for {Metrics.RecoverySustainSeconds} s."));
        return sb.ToString();
    }

    private static string Label(StrategySummary r) => r.Id switch
    {
        "A" => "A  immediate retry (UNSAFE)",
        "B" => "B  backoff 200/400/800 ms",
        "C" => "C  backoff + jitter + limits",
        _ => r.Id,
    };

    private static string Ms(double? value) => value is { } v ? string.Create(CultureInfo.InvariantCulture, $"{v:0} ms") : "-";
}

public sealed record StrategySummary(
    string Id, string Name, string Waiting, bool UnsafeBaseline, double? OperationDeadlineMs,
    OperationTotals Operations, AttemptTotals Attempts, DependencyTotals Dependency, LatencyByOutcome LatencyMs,
    PhaseTable ByArrivalPhase, IReadOnlyList<double?> SuccessPercentByArrivalSecond, Recovery Recovery, double RunSeconds, double MaxDispatchLagMs);

public sealed record OperationTotals(
    int Started, int Succeeded, int Failed, double SuccessPercent,
    IReadOnlyDictionary<string, long> FailedBy, IReadOnlyDictionary<string, long> AttemptsHistogram);

public sealed record AttemptTotals(
    long SentByClient, long ReceivedByDependency, double AmplificationClient, double AmplificationDependency,
    IReadOnlyDictionary<string, long> ResponsesByStatus, long Timeouts, long CutByDeadline, long HttpErrors);

public sealed record LatencySummary(int Count, double? P50, double? P95, double? P99, double? Max);

public sealed record LatencyByOutcome(LatencySummary All, LatencySummary Succeeded, LatencySummary Failed);

/// <summary>Logical operations grouped by their scheduled arrival: before, inside or after the failure window.</summary>
public sealed record PhaseSummary(double FromS, double ToS, int Started, int Succeeded, int Failed, double? SuccessPercent, double? AttemptsPerOperation);

public sealed record PhaseTable(PhaseSummary BeforeWindow, PhaseSummary InWindow, PhaseSummary AfterWindow);

public sealed record TimeseriesPoint(
    int T, long DependencyRequests, long Succeeded, long Failed, int InFlight,
    long Started, long AttemptsSent, long Rejected503, long Injected503, int Queued);
