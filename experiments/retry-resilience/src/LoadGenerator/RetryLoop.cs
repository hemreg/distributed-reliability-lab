using System.Net;

namespace LoadGenerator;

/// <summary>
/// The retry loop from the article, shared by strategies A, B and C so that the per-attempt timeout,
/// the attempt limit and the transient classification are identical. Only the wait before the next
/// attempt differs (<see cref="Backoff"/>).
/// </summary>
/// <remarks>
/// Differences from the article's <c>GetWithRetryAsync</c>, and nothing else:
/// <list type="number">
/// <item>The two lines that compute the delay are moved into the <paramref name="delayAfterFailedAttempt"/>
/// delegate. For C it is <see cref="Backoff.FullJitter"/>, which contains those two lines unchanged
/// except that a seeded <see cref="Random"/> replaces <c>Random.Shared</c> (reproducible runs).</item>
/// <item>The constants are exposed (<see cref="MaxAttempts"/>, <see cref="AttemptTimeout"/>) so the
/// report and the tests can refer to them.</item>
/// </list>
/// The total-time limit of strategy C is not in this method: the caller passes a
/// <c>cancellationToken</c> that is cancelled at the operation deadline, which the article's code
/// already honours (it stops retrying and the pending attempt or delay is cancelled).
/// </remarks>
public static class RetryLoop
{
    public const int MaxAttempts = 4;
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(2);

    public static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    public static async Task<HttpResponseMessage> GetWithRetryAsync(
        HttpClient client, string url, Func<int, TimeSpan> delayAfterFailedAttempt, CancellationToken cancellationToken)
    {
        const int maxAttempts = MaxAttempts;
        for (var attempt = 1; ; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(AttemptTimeout);
            HttpResponseMessage? response = null;
            try
            {
                response = await client.GetAsync(url, timeout.Token);
                if (!IsTransient(response.StatusCode) || attempt == maxAttempts) return response;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < maxAttempts) { }
            catch (HttpRequestException) when (attempt < maxAttempts) { }
            response?.Dispose();
            var delay = delayAfterFailedAttempt(attempt); // article: capMs = ...; delay = Random.Shared.Next(0, capMs + 1)
            await Task.Delay(delay, cancellationToken);
        }
    }
}

/// <summary>Wait before attempt <c>attempt + 1</c>, after attempt <c>attempt</c> failed transiently.</summary>
public static class Backoff
{
    /// <summary>A: retry at once.</summary>
    public static TimeSpan Immediate(int attempt) => TimeSpan.Zero;

    /// <summary>B: the article's exponential cap, without jitter: 200, 400, 800 ms (then 1600, 2000, 2000, ...).</summary>
    public static TimeSpan Exponential(int attempt) =>
        TimeSpan.FromMilliseconds(Math.Min(200 * (1 << (attempt - 1)), 2000));

    /// <summary>C: the article's full jitter, uniformly random in [0, cap] milliseconds.</summary>
    public static Func<int, TimeSpan> FullJitter(Random random) => attempt =>
    {
        var capMs = Math.Min(200 * (1 << (attempt - 1)), 2000);
        return TimeSpan.FromMilliseconds(random.Next(0, capMs + 1));
    };
}
