using System.Net;
using LoadGenerator;

namespace RetryResilience.Tests;

public sealed class BackoffTests
{
    // The article's caps: Math.Min(200 * (1 << (attempt - 1)), 2000).
    public static TheoryData<int, int> ArticleCaps => new()
    {
        { 1, 200 }, { 2, 400 }, { 3, 800 }, { 4, 1600 }, { 5, 2000 }, { 6, 2000 },
    };

    [Fact]
    public void A_immediate_retry_never_waits()
    {
        for (var attempt = 1; attempt <= 6; attempt++)
            Assert.Equal(TimeSpan.Zero, Backoff.Immediate(attempt));
    }

    [Fact]
    public void B_waits_200_400_800_ms_before_attempts_2_3_4()
    {
        var waits = Enumerable.Range(1, RetryLoop.MaxAttempts - 1).Select(a => Backoff.Exponential(a).TotalMilliseconds);
        Assert.Equal([200, 400, 800], waits);
    }

    [Theory]
    [MemberData(nameof(ArticleCaps))]
    public void B_is_the_article_cap_without_jitter(int attempt, int capMs) =>
        Assert.Equal(capMs, Backoff.Exponential(attempt).TotalMilliseconds);

    [Theory]
    [MemberData(nameof(ArticleCaps))]
    public void C_full_jitter_stays_within_0_and_the_article_cap_and_covers_the_range(int attempt, int capMs)
    {
        var delay = Backoff.FullJitter(new Random(42));
        var samples = Enumerable.Range(0, 20_000).Select(_ => delay(attempt).TotalMilliseconds).ToArray();

        Assert.All(samples, ms => Assert.InRange(ms, 0, capMs));
        Assert.All(samples, ms => Assert.Equal(Math.Floor(ms), ms)); // whole milliseconds, as Random.Next(0, cap + 1)
        Assert.Contains(0.0, samples);                               // both ends are reachable
        Assert.Contains((double)capMs, samples);
        Assert.InRange(samples.Average(), capMs * 0.45, capMs * 0.55); // uniform: mean about cap / 2
    }

    [Fact]
    public void C_is_reproducible_per_operation_and_differs_between_operations()
    {
        var c = Strategies.C(seed: 42, deadline: TimeSpan.FromSeconds(5));
        double[] Waits(int op)
        {
            var delay = c.DelayForOperation(op);
            return [.. Enumerable.Range(1, 3).Select(a => delay(a).TotalMilliseconds)];
        }

        Assert.Equal(Waits(7), Waits(7));
        Assert.NotEqual(Waits(7), Waits(8));
    }

    [Fact]
    public void Strategies_share_attempt_limit_and_timeout_only_C_has_a_total_deadline()
    {
        Assert.Equal(4, RetryLoop.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(2), RetryLoop.AttemptTimeout);
        Assert.Null(Strategies.A().OperationDeadline);
        Assert.Null(Strategies.B().OperationDeadline);
        Assert.Equal(TimeSpan.FromSeconds(5), Strategies.C(42, TimeSpan.FromSeconds(5)).OperationDeadline);
        Assert.True(Strategies.A().UnsafeBaseline);
    }
}

public sealed class TransientClassificationTests
{
    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]      // 408
    [InlineData(HttpStatusCode.TooManyRequests)]     // 429
    [InlineData(HttpStatusCode.BadGateway)]          // 502
    [InlineData(HttpStatusCode.ServiceUnavailable)]  // 503
    [InlineData(HttpStatusCode.GatewayTimeout)]      // 504
    public void Retries_these_statuses(HttpStatusCode status) => Assert.True(RetryLoop.IsTransient(status));

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotImplemented)]
    public void Does_not_retry_these_statuses(HttpStatusCode status) => Assert.False(RetryLoop.IsTransient(status));
}
