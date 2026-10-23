namespace LoadGenerator;

/// <param name="Id">A, B, C or D.</param>
/// <param name="Waiting">Human-readable wait schedule.</param>
/// <param name="UnsafeBaseline">True for A: it exists only to show the failure.</param>
/// <param name="OperationDeadline">Total-time limit for one logical operation; null = none (only the implicit bound of 4 attempts).</param>
/// <param name="DelayForOperation">Creates the delay function for logical operation number <c>i</c>.</param>
/// <param name="Budget">Strategy D: the shared retry budget of this run; null for A, B and C.</param>
public sealed record Strategy(
    string Id,
    string Name,
    string Waiting,
    bool UnsafeBaseline,
    TimeSpan? OperationDeadline,
    Func<int, Func<int, TimeSpan>> DelayForOperation,
    RetryBudget? Budget = null);

public static class Strategies
{
    /// <summary>A. E03-A: no deadline. E03-B passes the deadline every strategy shares.</summary>
    public static Strategy A(TimeSpan? deadline = null) => new(
        "A", "Immediate retry", WithDeadline("none: next attempt at once", deadline), UnsafeBaseline: true, OperationDeadline: deadline,
        _ => Backoff.Immediate);

    public static Strategy B(TimeSpan? deadline = null) => new(
        "B", "Exponential backoff", WithDeadline("200, 400, 800 ms (no jitter)", deadline), UnsafeBaseline: false, OperationDeadline: deadline,
        _ => Backoff.Exponential);

    /// <summary>
    /// Each logical operation gets its own Random, seeded from (seed, operation index): a shared
    /// Random is not thread-safe, and per-operation seeds keep op i's jitter identical across runs.
    /// </summary>
    public static Strategy C(int seed, TimeSpan deadline) => new(
        "C", "Exponential backoff + full jitter + limits",
        $"random 0-200, 0-400, 0-800 ms; total {deadline.TotalSeconds:0.#} s per operation",
        UnsafeBaseline: false, OperationDeadline: deadline,
        operation => JitterFor(seed, operation));

    /// <summary>
    /// D = C + retry budget: the same jitter as C for the same operation (same seeds), but before
    /// every retry the shared budget must grant a token; otherwise the operation ends at once.
    /// </summary>
    public static Strategy D(int seed, TimeSpan deadline, RetryBudget budget) => new(
        "D", "Full jitter + limits + retry budget",
        $"as C (total {deadline.TotalSeconds:0.#} s per operation); a retry needs a token: " +
        $"{budget.Ratio:0.##} per new operation, at most {budget.MaxTokens:0.#}, shared by all clients",
        UnsafeBaseline: false, OperationDeadline: deadline,
        operation =>
        {
            var jitter = JitterFor(seed, operation);
            return attempt => budget.TryRetry() ? jitter(attempt) : throw new RetryBudgetExhaustedException(attempt);
        },
        budget);

    private static Func<int, TimeSpan> JitterFor(int seed, int operation) =>
        Backoff.FullJitter(new Random(unchecked(seed * 1_000_003 + operation)));

    private static string WithDeadline(string waiting, TimeSpan? deadline) =>
        deadline is { } d ? $"{waiting}; total {d.TotalSeconds:0.#} s per operation" : waiting;

    /// <summary>E03-A: A and B without a deadline, C with one (unchanged since E03-A was first run).</summary>
    public static Strategy Parse(string id, int seed, TimeSpan cDeadline) => id.Trim().ToUpperInvariant() switch
    {
        "A" => A(),
        "B" => B(),
        "C" => C(seed, cDeadline),
        var other => throw new ArgumentException($"unknown strategy '{other}' (expected A, B or C)"),
    };

    /// <summary>E03-B: the same deadline for every strategy; D gets a fresh budget.</summary>
    public static Strategy ParseCorrelated(string id, int seed, TimeSpan deadline, double budgetRatio, double budgetMaxTokens) =>
        id.Trim().ToUpperInvariant() switch
        {
            "A" => A(deadline),
            "B" => B(deadline),
            "C" => C(seed, deadline),
            "D" => D(seed, deadline, new RetryBudget(budgetRatio, budgetMaxTokens)),
            var other => throw new ArgumentException($"unknown strategy '{other}' (expected A, B, C or D)"),
        };
}
