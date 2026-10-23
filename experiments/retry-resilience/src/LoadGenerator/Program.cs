using System.Globalization;
using LoadGenerator;

// E03 load generator. Runs the scenarios in E03_SCENARIOS (default "a,b") against the same dependency:
//   E03-A  transient failure under independent arrivals: strategies A, B, C, open-loop Poisson arrivals
//   E03-B  correlated failure and recovery pressure: strategies A, B, C, D, a closed-loop client population
// Each scenario writes results/e03-x/{summary.json, timeseries.json, console.txt} and prints its tables.
// Stops the dependency at the end, so `docker compose up` returns on its own.
//
// `LoadGenerator aggregate DIR` instead summarises repeated runs (./repeat-e03.sh).

if (args is ["aggregate", ..])
    return await Repeat.RunAsync(args.Length > 1 ? args[1] : "/results/repeat");

var options = LoadOptions.FromEnvironment();
using var admin = new DependencyAdmin(options.DependencyUrl);
var exitCode = 0;

try
{
    await admin.WaitUntilHealthyAsync(TimeSpan.FromSeconds(60));
    var probe = await admin.ResetAsync("probe", failure: false);
    ResultsDirectory.Create(options.ResultsDirectory);

    for (var i = 0; i < options.Scenarios.Count; i++)
    {
        if (i > 0) Console.WriteLine();
        if (options.Scenarios[i] == "a") await RunScenarioAAsync(probe.Config);
        else await RunScenarioBAsync(probe.Config, probe.Correlated ?? throw new InvalidOperationException("dependency did not report the E03-B timeline"));
    }
}
catch (Exception e)
{
    Console.Error.WriteLine($"E03 run failed: {e}");
    exitCode = 1;
}
finally
{
    // Stop the dependency too, so `docker compose up` ends when the experiment ends.
    await admin.ShutdownAsync();
}

return exitCode;

// E03-A: unchanged experiment (parameters, behaviour, output format); only its output directory is new.
async Task RunScenarioAAsync(DependencyConfig config)
{
    Console.WriteLine("#### E03-A Transient failure under independent arrivals -> results/e03-a/");
    var directory = ResultsDirectory.Create(Path.Combine(options.ResultsDirectory, "e03-a"));
    using var tee = ConsoleTee.Start(Path.Combine(directory, "console.txt"));
    var startedAt = DateTimeOffset.UtcNow;
    var strategies = options.StrategyIds.Select(id => Strategies.Parse(id, options.Seed, TimeSpan.FromMilliseconds(options.CDeadlineMs))).ToArray();
    var arrivals = ArrivalSchedule.Poisson(options.RatePerSecond, options.DurationSeconds, options.Seed);

    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"""
        E03 Retry Storm. LOCAL LAB ONLY: strategy A is a deliberately unsafe baseline.
        Workload: {arrivals.Length} logical operations, open-loop Poisson {options.RatePerSecond:0.#}/s for {options.DurationSeconds} s, seed {options.Seed}; identical for every strategy.
        Dependency: {config.Workers} workers x {config.ServiceTimeMs} ms = {config.CapacityPerSecond:0} req/s, queue {config.QueueLimit}, rejects with 503 beyond that.
        Failure window {config.FailureStartSeconds}-{config.FailureEndSeconds} s: service time {config.FailureServiceTimeMs} ms ({config.FailureCapacityPerSecond:0} req/s) and {config.FailureErrorRate * 100:0}% injected 503.
        Client: max {RetryLoop.MaxAttempts} attempts, {RetryLoop.AttemptTimeout.TotalSeconds:0} s per attempt, retry on 408/429/502/503/504, timeout, HttpRequestException.
        Each strategy: {options.WarmupSeconds} s warm-up (not measured), reset, {options.DurationSeconds} s measured run, drain.
        """));

    await using var attemptLog = options.AttemptLog ? new StreamWriter(Path.Combine(directory, "attempts.jsonl"), append: false) : null;

    var runs = new List<StrategyRun>();
    foreach (var strategy in strategies)
    {
        Console.WriteLine();
        Console.WriteLine($"[{strategy.Id}] {strategy.Name}{(strategy.UnsafeBaseline ? " (UNSAFE BASELINE)" : "")}: wait {strategy.Waiting}");
        var run = await StrategyRunner.RunAsync(strategy, arrivals, options, admin, attemptLog);
        runs.Add(run);
        var s = Report.Summarize(run, options.DurationSeconds);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  [{strategy.Id}] done in {run.DurationSeconds:0.0} s: {s.Operations.Succeeded}/{s.Operations.Started} ops succeeded, " +
            $"{s.Attempts.ReceivedByDependency} requests at the dependency ({s.Attempts.SentByClient} sent), max dispatch lag {run.MaxDispatchLagMs:0.0} ms"));
    }

    var summaries = runs.Select(r => Report.Summarize(r, options.DurationSeconds)).ToArray();
    await Report.WriteJsonAsync(Path.Combine(directory, "summary.json"), Report.Summary(options, config, arrivals.Length, summaries, startedAt));
    await Report.WriteJsonAsync(Path.Combine(directory, "timeseries.json"), Report.Timeseries(config, options.DurationSeconds, runs));

    Console.WriteLine();
    Console.Write(Report.Table(options, config, arrivals.Length, summaries));
    Console.WriteLine();
    Console.WriteLine("Wrote results/e03-a/summary.json and results/e03-a/timeseries.json" + (options.AttemptLog ? " and results/e03-a/attempts.jsonl" : "") + ".");
    Console.WriteLine("One run on one machine: compare strategies within a run, and repeat to see the variation.");
}

// E03-B: closed-loop clients, freeze and restart, A/B/C/D with the same deadline.
async Task RunScenarioBAsync(DependencyConfig config, DependencyCorrelatedConfig failure)
{
    var b = options.Correlated;
    Console.WriteLine("#### E03-B Correlated failure and recovery pressure -> results/e03-b/");
    var directory = ResultsDirectory.Create(Path.Combine(options.ResultsDirectory, "e03-b"));
    using var tee = ConsoleTee.Start(Path.Combine(directory, "console.txt"));
    var startedAt = DateTimeOffset.UtcNow;
    var deadline = TimeSpan.FromMilliseconds(b.DeadlineMs);
    var strategies = b.StrategyIds.Select(id => Strategies.ParseCorrelated(id, options.Seed, deadline, b.BudgetRatio, b.BudgetMaxTokens)).ToArray();

    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"""
        E03-B Correlated failure and recovery pressure. LOCAL LAB ONLY: strategy A is a deliberately unsafe baseline.
        Clients: {b.Clients}, closed loop: one logical operation at a time, then think {b.ThinkMinMs}-{b.ThinkMaxMs} ms (uniform), about {b.ApproximateRatePerSecond:0} ops/s; seed {options.Seed}; {b.DurationSeconds} s.
        Dependency: {config.Workers} workers x {config.ServiceTimeMs} ms = {config.CapacityPerSecond:0} req/s, queue {config.QueueLimit} (admits {config.Workers + config.QueueLimit}), rejects with 503 beyond that.
        Failure: freeze {failure.FreezeStartMs / 1000.0:0.0}-{failure.RestartMs / 1000.0:0.0} s (nothing completes, every request is held), restart at {failure.RestartMs / 1000.0:0.0} s: every request inside fails with 503 at that instant;
                 {(failure.OutageMs > 0 ? $"outage (all refused) until {failure.OutageEndMs / 1000.0:0.0} s, " : "")}cold start until {failure.ColdEndMs / 1000.0:0.0} s: service time {failure.ColdServiceTimeMs} ms ({failure.ColdCapacityPerSecond:0} req/s).
        Client: max {RetryLoop.MaxAttempts} attempts, {RetryLoop.AttemptTimeout.TotalSeconds:0} s per attempt, {b.DeadlineMs / 1000.0:0.#} s per logical operation for EVERY strategy; retry on 408/429/502/503/504, timeout, HttpRequestException.
        Each strategy: {options.WarmupSeconds} s warm-up (not measured), reset, {b.DurationSeconds} s measured run, drain.
        """));

    await using var attemptLog = options.AttemptLog ? new StreamWriter(Path.Combine(directory, "attempts.jsonl"), append: false) : null;

    var runs = new List<StrategyRun>();
    foreach (var strategy in strategies)
    {
        Console.WriteLine();
        Console.WriteLine($"[{strategy.Id}] {strategy.Name}{(strategy.UnsafeBaseline ? " (UNSAFE BASELINE)" : "")}: wait {strategy.Waiting}");
        var run = await ClosedLoopRunner.RunAsync(strategy, b, options, admin, attemptLog);
        runs.Add(run);
        var s = CorrelatedReport.Summarize(run, b);
        Console.WriteLine(CorrelatedReport.WaveLine(run, run.Dependency.Correlated ?? failure));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  [{strategy.Id}] done in {run.DurationSeconds:0.0} s: {s.Operations.Succeeded}/{s.Operations.Started} ops succeeded, " +
            $"{s.Load.RequestsAtDependency} requests at the dependency ({s.Load.AttemptsSent} sent), {s.Concurrency.HeldAtRestart} failed together at the restart, " +
            $"max wake-up lag {run.MaxDispatchLagMs:0.0} ms"));
    }

    var summaries = runs.Select(r => CorrelatedReport.Summarize(r, b)).ToArray();
    await CorrelatedReport.WriteJsonAsync(Path.Combine(directory, "summary.json"), CorrelatedReport.Summary(options, config, failure, summaries, startedAt));
    await CorrelatedReport.WriteJsonAsync(Path.Combine(directory, "timeseries.json"), CorrelatedReport.Timeseries(failure, b.DurationSeconds, runs));

    Console.WriteLine();
    Console.Write(CorrelatedReport.Table(options, config, failure, summaries));
    Console.WriteLine();
    Console.WriteLine("Wrote results/e03-b/summary.json and results/e03-b/timeseries.json" + (options.AttemptLog ? " and results/e03-b/attempts.jsonl" : "") + ".");
    Console.WriteLine("One run on one machine: compare strategies within a run; ./repeat-e03.sh repeats both scenarios over several seeds.");
}
