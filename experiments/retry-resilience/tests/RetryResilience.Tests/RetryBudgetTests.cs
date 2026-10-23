using System.Net;
using LoadGenerator;

namespace RetryResilience.Tests;

public sealed class RetryBudgetTests
{
    [Fact]
    public void Starts_full_and_grants_exactly_max_tokens_retries_then_denies()
    {
        var budget = new RetryBudget(ratio: 0.1, maxTokens: 10);
        for (var i = 0; i < 10; i++) Assert.True(budget.TryRetry());
        Assert.False(budget.TryRetry());
        Assert.False(budget.TryRetry());
        Assert.Equal(10, budget.Granted);
        Assert.Equal(2, budget.Denied);
    }

    [Fact]
    public void Ten_new_operations_at_ratio_0_1_pay_for_one_retry()
    {
        var budget = new RetryBudget(ratio: 0.1, maxTokens: 10);
        while (budget.TryRetry()) { } // empty the bucket

        for (var i = 0; i < 9; i++) budget.OnFirstAttempt();
        Assert.False(budget.TryRetry()); // 0.9 tokens: not a whole one
        budget.OnFirstAttempt();
        Assert.True(budget.TryRetry());  // 1.0 token (ten additions of 0.1, despite binary rounding)
        Assert.False(budget.TryRetry());
    }

    [Fact]
    public void Tokens_never_exceed_the_cap()
    {
        var budget = new RetryBudget(ratio: 0.5, maxTokens: 3);
        for (var i = 0; i < 1000; i++) budget.OnFirstAttempt();
        Assert.Equal(3, budget.Tokens, 9);
        Assert.Equal(1000, budget.FirstAttempts);
        for (var i = 0; i < 3; i++) Assert.True(budget.TryRetry());
        Assert.False(budget.TryRetry());
    }

    [Fact]
    public void Over_any_sequence_retries_stay_within_max_tokens_plus_ratio_times_new_operations()
    {
        var random = new Random(42);
        var budget = new RetryBudget(ratio: 0.1, maxTokens: 10);
        long operations = 0;
        for (var step = 0; step < 100_000; step++)
        {
            if (random.NextDouble() < 0.3) { budget.OnFirstAttempt(); operations++; }
            else budget.TryRetry(); // retries are asked for far more often than the budget allows
            Assert.True(budget.Granted <= 10 + 0.1 * operations + 1e-6, $"granted {budget.Granted} after {operations} operations");
        }
        // The budget is the binding limit here, so it is (almost) used up: about 10% of the operations.
        Assert.InRange(budget.Granted, 0.1 * operations, 10 + 0.1 * operations);
    }

    [Fact]
    public async Task Concurrent_callers_never_overspend()
    {
        var budget = new RetryBudget(ratio: 0.1, maxTokens: 10);
        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 10_000; i++)
            {
                budget.OnFirstAttempt();
                budget.TryRetry();
                budget.TryRetry();
            }
        }, TestContext.Current.CancellationToken));
        await Task.WhenAll(tasks);

        Assert.Equal(80_000, budget.FirstAttempts);
        Assert.Equal(160_000, budget.Granted + budget.Denied);
        Assert.InRange(budget.Granted, 8_000 - 1, 8_000 + 10); // 10% of 80,000 plus the initial 10
    }

    [Theory]
    [InlineData(-0.1, 10)]
    [InlineData(0.1, 0.5)]
    public void Rejects_invalid_parameters(double ratio, double maxTokens) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryBudget(ratio, maxTokens));
}

public sealed class StrategyDTests
{
    private sealed class AlwaysUnavailable : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }

    [Fact]
    public void D_waits_exactly_like_C_for_the_same_operation_while_tokens_last()
    {
        var c = Strategies.C(seed: 42, deadline: TimeSpan.FromSeconds(5));
        var d = Strategies.D(seed: 42, deadline: TimeSpan.FromSeconds(5), new RetryBudget(0.1, 10));
        for (var op = 0; op < 3; op++)
        {
            var cDelay = c.DelayForOperation(op);
            var dDelay = d.DelayForOperation(op);
            for (var attempt = 1; attempt <= 3; attempt++) Assert.Equal(cDelay(attempt), dDelay(attempt));
        }
    }

    [Fact]
    public void D_throws_instead_of_waiting_when_the_budget_is_empty()
    {
        var budget = new RetryBudget(0.1, 1);
        var delay = Strategies.D(42, TimeSpan.FromSeconds(5), budget).DelayForOperation(0);
        delay(1); // uses the only token
        var e = Assert.Throws<RetryBudgetExhaustedException>(() => delay(2));
        Assert.Equal(2, e.FailedAttempt);
    }

    [Fact]
    public async Task With_one_token_the_article_loop_sends_two_attempts_then_stops()
    {
        var handler = new AlwaysUnavailable();
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://dependency.test"), Timeout = Timeout.InfiniteTimeSpan };
        var d = Strategies.D(42, TimeSpan.FromSeconds(5), new RetryBudget(0.1, 1));

        var e = await Assert.ThrowsAsync<RetryBudgetExhaustedException>(() =>
            RetryLoop.GetWithRetryAsync(client, "/work", d.DelayForOperation(7), TestContext.Current.CancellationToken));
        Assert.Equal(2, handler.Calls);
        Assert.Equal(2, e.FailedAttempt);
    }

    [Fact]
    public async Task A_refused_retry_ends_the_operation_as_retry_budget_exhausted_and_is_counted()
    {
        var budget = new RetryBudget(0.1, 1);
        while (budget.TryRetry()) { }
        var handler = new AlwaysUnavailable();
        using var client = new HttpClient(new InstrumentedHandler(handler)) { BaseAddress = new Uri("http://dependency.test"), Timeout = Timeout.InfiniteTimeSpan };
        var recorder = new RunRecorder(1, "D", attemptLog: null);
        recorder.Start();

        // The operation deposits 0.1 token: not enough for a retry, so it ends after one attempt.
        await StrategyRunner.RunOperationAsync(client, Strategies.D(42, TimeSpan.FromSeconds(5), budget), recorder, 0, 0);

        var result = Assert.Single(recorder.Results);
        Assert.False(result.Succeeded);
        Assert.Equal("retry_budget_exhausted", result.Outcome);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(1, recorder.RetriesDenied);
        Assert.Equal(1, budget.FirstAttempts);
    }

    [Fact]
    public void E03_B_strategies_share_the_deadline_only_D_has_a_budget()
    {
        var deadline = TimeSpan.FromSeconds(5);
        var all = "ABCD".Select(id => Strategies.ParseCorrelated(id.ToString(), 42, deadline, 0.1, 10)).ToArray();
        Assert.All(all, s => Assert.Equal(deadline, s.OperationDeadline));
        Assert.Equal([false, false, false, true], all.Select(s => s.Budget is not null));
        Assert.True(all[0].UnsafeBaseline);
        Assert.All(all.Skip(1), s => Assert.False(s.UnsafeBaseline));
    }

    [Fact]
    public void E03_A_strategies_are_unchanged()
    {
        Assert.Null(Strategies.Parse("A", 42, TimeSpan.FromSeconds(5)).OperationDeadline);
        Assert.Null(Strategies.Parse("B", 42, TimeSpan.FromSeconds(5)).OperationDeadline);
        Assert.Equal("none: next attempt at once", Strategies.Parse("A", 42, TimeSpan.FromSeconds(5)).Waiting);
        Assert.Throws<ArgumentException>(() => Strategies.Parse("D", 42, TimeSpan.FromSeconds(5)));
    }
}
