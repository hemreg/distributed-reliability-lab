using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LoadGenerator;

/// <summary>
/// E03-B report: summary.json and timeseries.json (schema v2) and the console tables. The metrics
/// are kept separate on purpose (success, total load, instantaneous load, concurrency, latency,
/// recovery); none of them is folded into a single score.
/// </summary>
public static class CorrelatedReport
{
    public const string SummarySchema = "drlab-e03/summary/v2";
    public const string TimeseriesSchema = "drlab-e03/timeseries/v2";
    public const string ScenarioId = "e03-b";
    public const string Title = "Correlated failure and recovery pressure";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new CompactLongArrayConverter() },
    };

    public static Task WriteJsonAsync(string path, object value) => Report.WriteJsonAsync(path, value, Json);

    public static CorrelatedSummary Summarize(StrategyRun run, CorrelatedLoadOptions b)
    {
        var results = run.Recorder.Results.ToArray();
        var dep = run.Dependency;
        var failure = dep.Correlated ?? throw new InvalidOperationException("the dependency did not run the E03-B failure (scenario b)");
        var corrTotals = dep.CorrelatedTotals ?? throw new InvalidOperationException("E03-B totals missing");
        var fine = dep.Fine ?? [];
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

        PhaseSummary Phase(double fromMs, double toMs)
        {
            var inPhase = results.Where(r => r.ScheduledMs >= fromMs && r.ScheduledMs < toMs).ToArray();
            var ok = inPhase.Count(r => r.Succeeded);
            return new PhaseSummary(fromMs / 1000.0, toMs / 1000.0, inPhase.Length, ok, inPhase.Length - ok,
                inPhase.Length == 0 ? null : Math.Round(100.0 * ok / inPhase.Length, 2),
                inPhase.Length == 0 ? null : Math.Round((double)inPhase.Sum(r => r.Attempts) / inPhase.Length, 3));
        }

        var restartSecond = failure.RestartMs / 1000;
        var recovery = Metrics.RecoveryAfterWindow(run.Recorder.SucceededPerSecond, run.Recorder.FailedPerSecond, restartSecond, b.DurationSeconds - 1);

        var fineReceived = fine.Select(f => f.Received).ToArray();
        var (peakFine, peakFineAt) = Metrics.Peak(fineReceived);
        var duringRecovery = Metrics.SumBuckets(fineReceived, RunRecorder.FineBucketMs, failure.RestartMs, failure.ColdEndMs);

        var responses = run.Recorder.Responses.OrderBy(kv => kv.Key)
            .ToDictionary(kv => kv.Key.ToString(CultureInfo.InvariantCulture), kv => kv.Value);
        var attemptsSent = run.Recorder.AttemptsSent;

        return new CorrelatedSummary(
            Id: run.Strategy.Id,
            Name: run.Strategy.Name,
            Waiting: run.Strategy.Waiting,
            UnsafeBaseline: run.Strategy.UnsafeBaseline,
            OperationDeadlineMs: run.Strategy.OperationDeadline?.TotalMilliseconds,
            RetryBudget: run.Strategy.Budget is { } budget
                ? new BudgetSummary(budget.Ratio, budget.MaxTokens, budget.FirstAttempts, budget.Granted, budget.Denied, Math.Round(budget.Tokens, 3))
                : null,
            Operations: new OperationTotals(
                Started: ops,
                Succeeded: succeeded,
                Failed: ops - succeeded,
                SuccessPercent: ops == 0 ? 0 : Math.Round(100.0 * succeeded / ops, 2),
                FailedBy: results.Where(r => !r.Succeeded).GroupBy(r => r.Outcome).OrderBy(g => g.Key)
                    .ToDictionary(g => g.Key, g => (long)g.Count()),
                AttemptsHistogram: results.GroupBy(r => r.Attempts).OrderBy(g => g.Key)
                    .ToDictionary(g => g.Key.ToString(CultureInfo.InvariantCulture), g => (long)g.Count())),
            ByStartPhase: new CorrelatedPhases(
                BeforeFreeze: Phase(0, failure.FreezeStartMs),
                Affected: Phase(failure.FreezeStartMs, failure.ColdEndMs),
                AfterColdStart: Phase(failure.ColdEndMs, b.DurationSeconds * 1000.0)),
            Load: new LoadSummary(
                RequestsAtDependency: dep.Totals.Received,
                Amplification: ops == 0 ? 0 : Math.Round((double)dep.Totals.Received / ops, 3),
                AttemptsSent: attemptsSent,
                FirstAttempts: ops,
                RetriesSent: attemptsSent - ops,
                RetriesDenied: run.Recorder.RetriesDenied,
                RequestsDuringRecovery: duringRecovery,
                PeakRequestsPerSecond: dep.Totals.PeakReceivedPerSecond,
                PeakRequestsPer100Ms: peakFine,
                PeakRequestsPer100MsAtMs: peakFineAt * RunRecorder.FineBucketMs,
                ResponsesByStatus: responses,
                Timeouts: run.Recorder.Timeouts,
                CutByDeadline: run.Recorder.DeadlineCut,
                HttpErrors: run.Recorder.HttpErrors),
            Concurrency: new ConcurrencySummary(
                PeakActive: dep.Totals.PeakInFlight,
                HeldAtRestart: corrTotals.HeldAtRestart,
                PeakActiveAfterRestart: corrTotals.PeakInSystemAfterRestart),
            Dependency: new CorrelatedDependencyTotals(
                dep.Totals.Received, dep.Totals.Ok, dep.Totals.Rejected503, corrTotals.Restart503, corrTotals.Refused503,
                dep.Totals.Abandoned, corrTotals.RestartedAtMs),
            LatencyMs: new LatencyByOutcome(
                All: Latency(results),
                Succeeded: Latency(results.Where(r => r.Succeeded)),
                Failed: Latency(results.Where(r => !r.Succeeded))),
            Recovery: recovery,
            RunSeconds: Math.Round(run.DurationSeconds, 2),
            MaxWakeLagMs: Math.Round(run.MaxDispatchLagMs, 2),
            ClientClockOffsetMs: Math.Round(run.ClockOffsetMs, 2));
    }

    public static object Summary(LoadOptions options, DependencyConfig cfg, DependencyCorrelatedConfig failure,
        IReadOnlyList<CorrelatedSummary> strategies, DateTimeOffset startedAt)
    {
        var b = options.Correlated;
        return new
        {
            schema = SummarySchema,
            experiment = "E03 Retry Storm",
            scenario = new { id = ScenarioId, title = Title },
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
                    model = "closed loop: each client runs one logical operation at a time, then thinks",
                    clients = b.Clients,
                    thinkTimeMs = new { distribution = "uniform, whole ms, seeded per client", min = b.ThinkMinMs, max = b.ThinkMaxMs },
                    firstStartMs = new { distribution = "uniform per client", min = 0, max = b.ThinkMaxMs },
                    approximateOfferedRatePerSecond = Math.Round(b.ApproximateRatePerSecond, 1),
                    durationSeconds = b.DurationSeconds,
                    seed = options.Seed,
                    warmupSeconds = options.WarmupSeconds,
                    order = b.StrategyIds,
                },
                dependency = new
                {
                    workers = cfg.Workers,
                    serviceTimeMs = cfg.ServiceTimeMs,
                    queueLimit = cfg.QueueLimit,
                    admissionLimit = cfg.Workers + cfg.QueueLimit,
                    capacityPerSecond = cfg.CapacityPerSecond,
                },
                failure = new
                {
                    description = "freeze (nothing completes, every request is held), then a restart that fails every request inside "
                        + "with 503 at the same instant, then an optional outage (everything refused), then a cold start with slower service",
                    failure.FreezeStartMs,
                    failure.RestartMs,
                    failure.OutageEndMs,
                    failure.ColdEndMs,
                    failure.FreezeMs,
                    failure.OutageMs,
                    failure.ColdMs,
                    failure.ColdServiceTimeMs,
                    failure.ColdCapacityPerSecond,
                },
                client = new
                {
                    maxAttempts = RetryLoop.MaxAttempts,
                    attemptTimeoutMs = RetryLoop.AttemptTimeout.TotalMilliseconds,
                    retryOn = new[] { "408", "429", "502", "503", "504", "attempt timeout", "HttpRequestException" },
                    operationDeadlineMs = b.DeadlineMs,
                    operationDeadlineAppliesTo = "every strategy",
                    retryBudget = new
                    {
                        appliesTo = "D",
                        ratio = b.BudgetRatio,
                        maxTokens = b.BudgetMaxTokens,
                        initialTokens = b.BudgetMaxTokens,
                        scope = "one bucket shared by all clients of the run (one calling service)",
                        rule = "each new logical operation adds ratio tokens (capped at maxTokens); a retry needs and removes 1 token; without a token the operation ends",
                    },
                },
                recovery = new
                {
                    definition = "first second s >= the restart second where the per-second success ratio of completed logical operations "
                        + $">= {Metrics.RecoveryThreshold:0.00} for {Metrics.RecoverySustainSeconds} consecutive seconds; recovery = s - restart second",
                    fromSecond = failure.RestartMs / 1000,
                    threshold = Metrics.RecoveryThreshold,
                    sustainSeconds = Metrics.RecoverySustainSeconds,
                },
                affectedOperations = new
                {
                    definition = "logical operations that started from the freeze until the end of the cold start",
                    fromMs = failure.FreezeStartMs,
                    toMs = failure.ColdEndMs,
                },
                requestsDuringRecovery = new
                {
                    definition = "requests received by the dependency from the restart until the end of the cold start",
                    fromMs = failure.RestartMs,
                    toMs = failure.ColdEndMs,
                },
            },
            strategies,
        };
    }

    public static object Timeseries(DependencyCorrelatedConfig failure, int durationSeconds, IReadOnlyList<StrategyRun> runs) => new
    {
        schema = TimeseriesSchema,
        scenario = ScenarioId,
        bucketSeconds = 1,
        fineBucketMs = RunRecorder.FineBucketMs,
        failure = new
        {
            freezeStartMs = failure.FreezeStartMs,
            restartMs = failure.RestartMs,
            outageEndMs = failure.OutageEndMs,
            coldEndMs = failure.ColdEndMs,
            phases = new object[]
            {
                new { name = "normal", fromMs = 0, toMs = failure.FreezeStartMs },
                new { name = "frozen", fromMs = failure.FreezeStartMs, toMs = failure.RestartMs },
                new { name = "outage", fromMs = failure.RestartMs, toMs = failure.OutageEndMs },
                new { name = "cold", fromMs = failure.OutageEndMs, toMs = failure.ColdEndMs },
                new { name = "normal", fromMs = failure.ColdEndMs, toMs = (int?)null },
            },
        },
        arrivalsEndS = durationSeconds,
        fields = new Dictionary<string, string>
        {
            ["t"] = "second since the run started; the bucket covers [t, t+1)",
            ["dependencyRequests"] = "requests received by the dependency in this second (measured at the dependency, admitted + rejected + refused)",
            ["succeeded"] = "logical operations that completed successfully in this second (client side)",
            ["failed"] = "logical operations that completed with a failure in this second (client side)",
            ["inFlight"] = "peak number of requests inside the dependency (in service + queued + held by the freeze) in this second",
            ["started"] = "logical operations that started in this second (closed loop: depends on the strategy)",
            ["attemptsSent"] = "attempts the client sent in this second (cross-check for dependencyRequests)",
            ["retriesSent"] = "attempts number 2 to 4 sent in this second",
            ["retriesDenied"] = "strategy D: retries the budget refused in this second",
            ["rejected503"] = "requests the dependency rejected at once because workers and queue were full",
            ["restart503"] = "requests failed by the restart (all in the restart's second)",
            ["refused503"] = "requests refused during the outage after the restart (0 when the outage is 0 ms)",
            ["queued"] = "peak number of requests waiting in the dependency's queue in this second (held requests count during the freeze)",
        },
        fineFields = new Dictionary<string, string>
        {
            ["fromMs"] = "start of the first bucket; element i of every array covers [fromMs + i*100, fromMs + (i+1)*100) ms",
            ["dependencyRequests"] = "requests received by the dependency (admitted + rejected + refused)",
            ["active"] = "peak number of requests inside the dependency (in service + queued + held)",
            ["queued"] = "peak number of requests waiting for a worker",
            ["ok"] = "200 responses completed",
            ["rejected503"] = "rejected at once: workers and queue full",
            ["restart503"] = "failed by the restart",
            ["refused503"] = "refused during the outage",
            ["attempt1"] = "first attempts sent by the client (new logical operations)",
            ["attempt2"] = "second attempts sent (first retry)",
            ["attempt3"] = "third attempts sent",
            ["attempt4"] = "fourth attempts sent",
            ["retriesDenied"] = "strategy D: retries refused by the budget",
            ["succeeded"] = "logical operations completed successfully",
            ["failed"] = "logical operations completed with a failure",
        },
        strategies = runs.Select(run =>
        {
            var rec = run.Recorder;
            var dep = run.Dependency;
            var fine = dep.Fine ?? [];
            var started = new long[RunRecorder.MaxSeconds];
            foreach (var r in rec.Results) started[RunRecorder.Bucket(r.ScheduledMs)]++;

            var lastClientFine = LastNonZero(rec.FineAttempts(1), rec.FineSucceeded, rec.FineFailed, rec.FineRetriesDenied);
            var fineLength = Math.Max(fine.Count, lastClientFine + 1);
            var byIndex = fine.ToDictionary(f => f.Index);
            long[] Dep(Func<DependencyFine, long> field) =>
                [.. Enumerable.Range(0, fineLength).Select(i => byIndex.TryGetValue(i, out var f) ? field(f) : 0)];
            long[] Client(IReadOnlyList<long> series) => [.. Enumerable.Range(0, fineLength).Select(i => series[i])];

            var restart503 = Dep(f => f.Restart503);
            var refused503 = Dep(f => f.Refused503);
            long SumSecond(long[] perFine, int second) =>
                Enumerable.Range(second * 10, 10).Where(i => i < perFine.Length).Sum(i => perFine[i]);

            var bySecond = dep.Seconds.ToDictionary(s => s.T);
            var lastSecond = Math.Max(Math.Max(dep.Seconds.Count - 1, (fineLength - 1) / 10),
                LastNonZero(rec.AttemptsPerSecond, rec.SucceededPerSecond, rec.FailedPerSecond));

            return new
            {
                id = run.Strategy.Id,
                name = run.Strategy.Name,
                restartedAtMs = dep.CorrelatedTotals?.RestartedAtMs,
                points = Enumerable.Range(0, lastSecond + 1).Select(t =>
                {
                    var d = bySecond.GetValueOrDefault(t);
                    return new CorrelatedPoint(t, d?.Received ?? 0, rec.SucceededPerSecond[t], rec.FailedPerSecond[t], d?.PeakInFlight ?? 0,
                        started[t], rec.AttemptsPerSecond[t], rec.RetriesPerSecond[t], rec.RetriesDeniedPerSecond[t],
                        d?.Rejected503 ?? 0, SumSecond(restart503, t), SumSecond(refused503, t), d?.PeakQueued ?? 0);
                }).ToArray(),
                fine = new
                {
                    fromMs = 0,
                    dependencyRequests = Dep(f => f.Received),
                    active = Dep(f => f.PeakInSystem),
                    queued = Dep(f => f.PeakQueued),
                    ok = Dep(f => f.Ok),
                    rejected503 = Dep(f => f.Rejected503),
                    restart503,
                    refused503,
                    attempt1 = Client(rec.FineAttempts(1)),
                    attempt2 = Client(rec.FineAttempts(2)),
                    attempt3 = Client(rec.FineAttempts(3)),
                    attempt4 = Client(rec.FineAttempts(4)),
                    retriesDenied = Client(rec.FineRetriesDenied),
                    succeeded = Client(rec.FineSucceeded),
                    failed = Client(rec.FineFailed),
                },
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

    /// <summary>Console line: requests at the dependency per 100 ms around the restart.</summary>
    public static string WaveLine(StrategyRun run, DependencyCorrelatedConfig failure, int beforeMs = 500, int afterMs = 2000)
    {
        var fine = (run.Dependency.Fine ?? []).ToDictionary(f => f.Index, f => f.Received);
        var from = Math.Max(0, failure.RestartMs - beforeMs) / RunRecorder.FineBucketMs;
        var to = (failure.RestartMs + afterMs) / RunRecorder.FineBucketMs;
        var values = Enumerable.Range(from, to - from).Select(i => fine.GetValueOrDefault(i).ToString(CultureInfo.InvariantCulture));
        return string.Create(CultureInfo.InvariantCulture,
            $"  [{run.Strategy.Id}] requests at the dependency per 100 ms, {from / 10.0:0.0}-{to / 10.0:0.0} s (restart at {failure.RestartMs / 1000.0:0.0} s): {string.Join(' ', values)}");
    }

    /// <summary>The console summary: outcome and load as two separate tables.</summary>
    public static string Table(LoadOptions options, DependencyConfig cfg, DependencyCorrelatedConfig failure, IReadOnlyList<CorrelatedSummary> rows)
    {
        var ic = CultureInfo.InvariantCulture;
        var b = options.Correlated;
        var sb = new StringBuilder();
        const int width = 100;
        var rule = new string('=', width);
        sb.AppendLine(rule);
        sb.AppendLine($"E03-B {Title}: same clients, same freeze and restart, {rows.Count} client strategies");
        sb.AppendLine(rule);

        sb.AppendLine(string.Create(ic, $"{"Outcome",-30}{"Ops",7}{"Success",9}{"affected",10}{"p95",9}{"p99",9}{"Recovery",10}"));
        sb.AppendLine(new string('-', width));
        foreach (var r in rows)
        {
            var recovery = r.Recovery.Recovered
                ? $"{r.Recovery.Seconds} s"
                : $">{b.DurationSeconds - failure.RestartMs / 1000 - Metrics.RecoverySustainSeconds} s";
            sb.AppendLine(string.Create(ic,
                $"{Label(r),-30}{r.Operations.Started,7}{r.Operations.SuccessPercent,8:0.0}%{r.ByStartPhase.Affected.SuccessPercent,9:0.0}%" +
                $"{Ms(r.LatencyMs.All.P95),9}{Ms(r.LatencyMs.All.P99),9}{recovery,10}"));
        }
        sb.AppendLine();
        var recoveryHeader = string.Create(ic, $"{failure.RestartMs / 1000.0:0.#}-{failure.ColdEndMs / 1000.0:0.#} s");
        sb.AppendLine(string.Create(ic,
            $"{"Load at the dependency",-30}{"Requests",9}{"Ampl.",7}{"Retries",9}{"Denied",8}{recoveryHeader,9}{"Peak/s",8}{"Peak/100ms",11}{"Active*",9}"));
        sb.AppendLine(new string('-', width));
        foreach (var r in rows)
        {
            sb.AppendLine(string.Create(ic,
                $"{Label(r),-30}{r.Load.RequestsAtDependency,9}{r.Load.Amplification,6:0.00}x{r.Load.RetriesSent,9}{r.Load.RetriesDenied,8}" +
                $"{r.Load.RequestsDuringRecovery,9}{r.Load.PeakRequestsPerSecond,8}{r.Load.PeakRequestsPer100Ms,11}{r.Concurrency.PeakActiveAfterRestart,9}"));
        }
        sb.AppendLine(new string('-', width));
        sb.AppendLine(string.Create(ic,
            $"{b.Clients} clients, closed loop, think {b.ThinkMinMs / 1000.0:0.#}-{b.ThinkMaxMs / 1000.0:0.#} s (about {b.ApproximateRatePerSecond:0} ops/s), " +
            $"{b.DurationSeconds} s, seed {options.Seed}. Dependency {cfg.CapacityPerSecond:0} req/s ({cfg.Workers} x {cfg.ServiceTimeMs} ms), queue {cfg.QueueLimit}."));
        var outage = failure.OutageMs > 0 ? string.Create(ic, $", outage until {failure.OutageEndMs / 1000.0:0.0} s") : "";
        sb.AppendLine(string.Create(ic,
            $"Freeze {failure.FreezeStartMs / 1000.0:0.0}-{failure.RestartMs / 1000.0:0.0} s, restart at {failure.RestartMs / 1000.0:0.0} s fails every request inside{outage}" +
            $", cold start until {failure.ColdEndMs / 1000.0:0.0} s: {failure.ColdCapacityPerSecond:0} req/s ({failure.ColdServiceTimeMs} ms)."));
        sb.AppendLine(string.Create(ic,
            $"All: {RetryLoop.MaxAttempts} attempts max, {RetryLoop.AttemptTimeout.TotalSeconds:0} s each, {b.DeadlineMs / 1000.0:0.#} s per op. " +
            $"D: retry budget {b.BudgetRatio:0.##} token per new op, at most {b.BudgetMaxTokens:0.#}, shared."));
        sb.AppendLine(string.Create(ic,
            $"affected: success of ops started {failure.FreezeStartMs / 1000.0:0.0}-{failure.ColdEndMs / 1000.0:0.0} s. p95/p99: end-to-end latency of all ops, succeeded and failed. Recovery: s after the restart second until per-second success >= {Metrics.RecoveryThreshold * 100:0}% for {Metrics.RecoverySustainSeconds} s."));
        sb.AppendLine(string.Create(ic,
            $"Requests: received by the dependency, first attempts and retries; Ampl.: per logical op; {failure.RestartMs / 1000.0:0.#}-{failure.ColdEndMs / 1000.0:0.#} s: requests from the restart to the end of the cold start;"));
        sb.AppendLine("Peak/s, Peak/100ms: highest request count in one bucket; Active*: peak requests inside the restarted dependency.");
        return sb.ToString();
    }

    private static string Label(CorrelatedSummary r) => r.Id switch
    {
        "A" => "A  immediate retry (UNSAFE)",
        "B" => "B  backoff 200/400/800 ms",
        "C" => "C  backoff + full jitter",
        "D" => "D  C + retry budget",
        _ => r.Id,
    };

    private static string Ms(double? value) => value is { } v ? string.Create(CultureInfo.InvariantCulture, $"{v:0} ms") : "-";
}

public sealed record CorrelatedSummary(
    string Id, string Name, string Waiting, bool UnsafeBaseline, double? OperationDeadlineMs, BudgetSummary? RetryBudget,
    OperationTotals Operations, CorrelatedPhases ByStartPhase, LoadSummary Load, ConcurrencySummary Concurrency,
    CorrelatedDependencyTotals Dependency, LatencyByOutcome LatencyMs, Recovery Recovery, double RunSeconds, double MaxWakeLagMs,
    double ClientClockOffsetMs);

/// <param name="FirstAttempts">New logical operations seen by the budget (each deposited Ratio tokens).</param>
/// <param name="TokensLeft">Tokens in the bucket at the end of the run.</param>
public sealed record BudgetSummary(double Ratio, double MaxTokens, long FirstAttempts, long Granted, long Denied, double TokensLeft);

/// <summary>Logical operations grouped by when they started: before the freeze, freeze to end of cold start, after.</summary>
public sealed record CorrelatedPhases(PhaseSummary BeforeFreeze, PhaseSummary Affected, PhaseSummary AfterColdStart);

/// <param name="RequestsAtDependency">Total load: requests received by the dependency during the run (and its drain).</param>
/// <param name="RequestsDuringRecovery">Requests received from the restart until the end of the cold start.</param>
/// <param name="PeakRequestsPerSecond">Instantaneous load, 1 s buckets.</param>
/// <param name="PeakRequestsPer100Ms">Instantaneous load, 100 ms buckets; <see cref="PeakRequestsPer100MsAtMs"/> is the bucket's start.</param>
public sealed record LoadSummary(
    long RequestsAtDependency, double Amplification, long AttemptsSent, long FirstAttempts, long RetriesSent, long RetriesDenied,
    long RequestsDuringRecovery, long PeakRequestsPerSecond, long PeakRequestsPer100Ms, int PeakRequestsPer100MsAtMs,
    IReadOnlyDictionary<string, long> ResponsesByStatus, long Timeouts, long CutByDeadline, long HttpErrors);

/// <param name="PeakActive">Highest number of requests inside the dependency at any time (includes the requests held by the freeze).</param>
/// <param name="HeldAtRestart">Requests inside the old process at the restart; all failed at that instant (the synchronised cohort).</param>
/// <param name="PeakActiveAfterRestart">Highest number of requests inside the restarted process (in service + queued).</param>
public sealed record ConcurrencySummary(int PeakActive, int HeldAtRestart, int PeakActiveAfterRestart);

public sealed record CorrelatedDependencyTotals(
    long Received, long Ok, long Rejected503, long Restart503, long Refused503, long Abandoned, double? RestartedAtMs);

public sealed record CorrelatedPoint(
    int T, long DependencyRequests, long Succeeded, long Failed, int InFlight, long Started, long AttemptsSent,
    long RetriesSent, long RetriesDenied, long Rejected503, long Restart503, long Refused503, int Queued);

/// <summary>Writes long[] on one line inside indented JSON, so the 100 ms series stay readable.</summary>
public sealed class CompactLongArrayConverter : JsonConverter<long[]>
{
    public override long[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("expected an array");
        var values = new List<long>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) values.Add(reader.GetInt64());
        return [.. values];
    }

    public override void Write(Utf8JsonWriter writer, long[] value, JsonSerializerOptions options) =>
        writer.WriteRawValue("[" + string.Join(',', value.Select(v => v.ToString(CultureInfo.InvariantCulture))) + "]", skipInputValidation: true);
}
