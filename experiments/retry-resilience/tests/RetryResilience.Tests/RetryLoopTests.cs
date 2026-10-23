using System.Diagnostics;
using System.Net;
using LoadGenerator;

namespace RetryResilience.Tests;

/// <summary>The shared retry loop against a scripted in-memory handler (no network).</summary>
public sealed class RetryLoopTests
{
    private sealed class ScriptedHandler(Func<int, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        private int _calls;
        public List<TimeSpan> SentAt { get; } = [];
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        public int Calls => _calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            SentAt.Add(_clock.Elapsed);
            return respond(++_calls, cancellationToken);
        }
    }

    private static Task<HttpResponseMessage> Status(HttpStatusCode code) => Task.FromResult(new HttpResponseMessage(code));

    private static HttpClient Client(ScriptedHandler handler) =>
        new(handler) { BaseAddress = new Uri("http://dependency.test"), Timeout = Timeout.InfiniteTimeSpan };

    [Fact]
    public async Task Retries_503_until_success()
    {
        var handler = new ScriptedHandler((n, _) => Status(n < 3 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
        using var response = await RetryLoop.GetWithRetryAsync(Client(handler), "/work", Backoff.Immediate, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task Stops_after_4_attempts_and_returns_the_last_transient_response()
    {
        var handler = new ScriptedHandler((_, _) => Status(HttpStatusCode.ServiceUnavailable));
        using var response = await RetryLoop.GetWithRetryAsync(Client(handler), "/work", Backoff.Immediate, CancellationToken.None);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(4, handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Does_not_retry_non_transient_status(HttpStatusCode status)
    {
        var handler = new ScriptedHandler((_, _) => Status(status));
        using var response = await RetryLoop.GetWithRetryAsync(Client(handler), "/work", Backoff.Immediate, CancellationToken.None);
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Retries_HttpRequestException_and_rethrows_it_on_the_last_attempt()
    {
        var handler = new ScriptedHandler((_, _) => throw new HttpRequestException("connection refused"));
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            RetryLoop.GetWithRetryAsync(Client(handler), "/work", Backoff.Immediate, CancellationToken.None));
        Assert.Equal(4, handler.Calls);
    }

    [Fact]
    public async Task An_attempt_that_exceeds_2_s_is_cancelled_and_retried()
    {
        var handler = new ScriptedHandler(async (n, ct) =>
        {
            if (n == 1) await Task.Delay(Timeout.Infinite, ct); // hangs until the per-attempt timeout fires
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var clock = Stopwatch.StartNew();
        using var response = await RetryLoop.GetWithRetryAsync(Client(handler), "/work", Backoff.Immediate, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.Calls);
        Assert.InRange(clock.Elapsed.TotalSeconds, 1.9, 4.0);
    }

    [Fact]
    public async Task B_waits_at_least_200_400_800_ms_between_attempts()
    {
        var handler = new ScriptedHandler((_, _) => Status(HttpStatusCode.ServiceUnavailable));
        using var _ = await RetryLoop.GetWithRetryAsync(Client(handler), "/work", Backoff.Exponential, CancellationToken.None);
        var gaps = handler.SentAt.Zip(handler.SentAt.Skip(1), (a, b) => (b - a).TotalMilliseconds).ToArray();
        Assert.Equal(3, gaps.Length);
        Assert.InRange(gaps[0], 195, 450);
        Assert.InRange(gaps[1], 395, 650);
        Assert.InRange(gaps[2], 795, 1050);
    }

    [Fact]
    public async Task Operation_deadline_stops_retrying_and_cancels_the_pending_wait()
    {
        // Wait 10 s after the first failure, but the operation may only take 300 ms.
        var handler = new ScriptedHandler((_, _) => Status(HttpStatusCode.ServiceUnavailable));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var clock = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RetryLoop.GetWithRetryAsync(Client(handler), "/work", _ => TimeSpan.FromSeconds(10), deadline.Token));
        Assert.Equal(1, handler.Calls);
        Assert.InRange(clock.Elapsed.TotalMilliseconds, 250, 2000);
    }

    [Fact]
    public async Task Operation_deadline_cancels_an_attempt_in_flight_without_retrying()
    {
        var handler = new ScriptedHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RetryLoop.GetWithRetryAsync(Client(handler), "/work", Backoff.Immediate, deadline.Token));
        Assert.Equal(1, handler.Calls);
    }
}
