using System.Globalization;

namespace LoadGenerator;

/// <summary>Load generator settings from E03_* environment variables (compose.yaml sets them explicitly).</summary>
/// <param name="Scenarios">Which scenarios to run, in order: "a" (E03-A) and/or "b" (E03-B).</param>
/// <param name="ResultsDirectory">Root of the output; each scenario writes into its own subdirectory (e03-a, e03-b).</param>
/// <param name="Correlated">E03-B settings (E03B_* variables).</param>
public sealed record LoadOptions(
    Uri DependencyUrl,
    double RatePerSecond,
    int DurationSeconds,
    int WarmupSeconds,
    int Seed,
    int CDeadlineMs,
    IReadOnlyList<string> StrategyIds,
    string ResultsDirectory,
    bool AttemptLog,
    IReadOnlyList<string> Scenarios,
    CorrelatedLoadOptions Correlated)
{
    public static LoadOptions FromEnvironment()
    {
        static string? Env(string key) => Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : null;
        static int Int(string key, int fallback) => Env(key) is { } raw ? int.Parse(raw, CultureInfo.InvariantCulture) : fallback;
        static double Double(string key, double fallback) => Env(key) is { } raw ? double.Parse(raw, CultureInfo.InvariantCulture) : fallback;
        static string[] List(string key, string fallback) =>
            (Env(key) ?? fallback).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var options = new LoadOptions(
            DependencyUrl: new Uri(Env("E03_DEPENDENCY_URL") ?? "http://dependency:8080"),
            RatePerSecond: Double("E03_RATE", 100),
            DurationSeconds: Int("E03_DURATION_S", 30),
            WarmupSeconds: Int("E03_WARMUP_S", 2),
            Seed: Int("E03_SEED", 42),
            CDeadlineMs: Int("E03_C_DEADLINE_MS", 5000),
            StrategyIds: List("E03_STRATEGIES", "A,B,C"),
            ResultsDirectory: Env("E03_RESULTS_DIR") ?? "/results",
            AttemptLog: Env("E03_ATTEMPT_LOG") is "1" or "true" or "TRUE" or "True",
            Scenarios: [.. List("E03_SCENARIOS", "a,b").Select(s => s.ToLowerInvariant())],
            Correlated: new CorrelatedLoadOptions(
                Clients: Int("E03B_CLIENTS", 200),
                ThinkMinMs: Int("E03B_THINK_MIN_MS", 1000),
                ThinkMaxMs: Int("E03B_THINK_MAX_MS", 3000),
                DurationSeconds: Int("E03B_DURATION_S", 30),
                DeadlineMs: Int("E03B_DEADLINE_MS", 5000),
                StrategyIds: List("E03B_STRATEGIES", "A,B,C,D"),
                BudgetRatio: Double("E03B_BUDGET_RATIO", 0.1),
                BudgetMaxTokens: Double("E03B_BUDGET_MAX_TOKENS", 10)));

        if (options.RatePerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(RatePerSecond));
        if (options.DurationSeconds < 1) throw new ArgumentOutOfRangeException(nameof(DurationSeconds));
        if (options.CDeadlineMs < 1) throw new ArgumentOutOfRangeException(nameof(CDeadlineMs));
        foreach (var scenario in options.Scenarios)
            if (scenario is not ("a" or "b")) throw new ArgumentException($"unknown scenario '{scenario}' in E03_SCENARIOS (expected a, b)");
        options.Correlated.Validate();
        return options;
    }
}

/// <summary>E03-B: a closed-loop client population against the correlated failure.</summary>
/// <param name="Clients">Number of clients. Each runs one logical operation at a time, then thinks.</param>
/// <param name="ThinkMinMs">Think time after each operation, uniform in [min, max] ms (seeded per client).</param>
/// <param name="DurationSeconds">Clients start no new operation after this; the run then drains.</param>
/// <param name="DeadlineMs">Total time per logical operation, the same for every E03-B strategy.</param>
/// <param name="BudgetRatio">Strategy D: retry tokens earned per new logical operation (0.1 = retries up to ~10% of operations).</param>
/// <param name="BudgetMaxTokens">Strategy D: bucket size; also the initial fill.</param>
public sealed record CorrelatedLoadOptions(
    int Clients,
    int ThinkMinMs,
    int ThinkMaxMs,
    int DurationSeconds,
    int DeadlineMs,
    IReadOnlyList<string> StrategyIds,
    double BudgetRatio,
    double BudgetMaxTokens)
{
    /// <summary>Approximate offered load: clients / mean think time (operations are short in normal operation).</summary>
    public double ApproximateRatePerSecond => Clients * 1000.0 / Math.Max(1, (ThinkMinMs + ThinkMaxMs) / 2.0);

    public void Validate()
    {
        if (Clients < 1) throw new ArgumentOutOfRangeException(nameof(Clients), Clients, "must be >= 1");
        if (ThinkMinMs < 0 || ThinkMaxMs < ThinkMinMs) throw new ArgumentException($"think time [{ThinkMinMs}, {ThinkMaxMs}] ms is invalid");
        if (DurationSeconds < 1) throw new ArgumentOutOfRangeException(nameof(DurationSeconds), DurationSeconds, "must be >= 1");
        if (DeadlineMs < 1) throw new ArgumentOutOfRangeException(nameof(DeadlineMs), DeadlineMs, "must be >= 1");
        if (BudgetRatio < 0) throw new ArgumentOutOfRangeException(nameof(BudgetRatio), BudgetRatio, "must be >= 0");
        if (BudgetMaxTokens < 1) throw new ArgumentOutOfRangeException(nameof(BudgetMaxTokens), BudgetMaxTokens, "must be >= 1");
    }
}
