namespace LoadGenerator;

/// <summary>
/// Client-side retry budget (strategy D), a token bucket shared by every client of the run:
/// each new logical operation (its first attempt) deposits <see cref="Ratio"/> tokens, each retry
/// needs one whole token and withdraws it, and the bucket holds at most <see cref="MaxTokens"/>
/// (it starts full). Over any period, retries are therefore at most
/// <c>MaxTokens + Ratio x new operations</c>: with 0.1 and 10, about 10% of the first attempts
/// plus a burst of 10.
/// </summary>
/// <remarks>
/// The ratio follows the per-client retry budget in Google's SRE book ("Handling Overload": retry only
/// while retries are below 10% of requests). The deposit/withdraw token bucket is the form used by
/// Finagle's <c>RetryBudget</c> (percentCanRetry). gRPC's retry throttling (gRFC A6) is a close
/// variant that adds tokens on success and removes them on failure. The AWS Builders' Library
/// article "Timeouts, retries, and backoff with jitter" describes limiting retries locally with a
/// token bucket. None of those implementations is used here; this class is the lab's own, minimal one.
/// </remarks>
public sealed class RetryBudget
{
    private readonly Lock _lock = new();
    private double _tokens;
    private long _firstAttempts, _granted, _denied;

    public RetryBudget(double ratio, double maxTokens)
    {
        if (ratio < 0) throw new ArgumentOutOfRangeException(nameof(ratio), ratio, "must be >= 0");
        if (maxTokens < 1) throw new ArgumentOutOfRangeException(nameof(maxTokens), maxTokens, "must be >= 1");
        Ratio = ratio;
        MaxTokens = maxTokens;
        _tokens = maxTokens;
    }

    public double Ratio { get; }
    public double MaxTokens { get; }

    public double Tokens { get { lock (_lock) return _tokens; } }
    public long FirstAttempts { get { lock (_lock) return _firstAttempts; } }
    public long Granted { get { lock (_lock) return _granted; } }
    public long Denied { get { lock (_lock) return _denied; } }

    /// <summary>A new logical operation starts (its first attempt is never subject to the budget).</summary>
    public void OnFirstAttempt()
    {
        lock (_lock)
        {
            _firstAttempts++;
            _tokens = Math.Min(MaxTokens, _tokens + Ratio);
        }
    }

    /// <summary>Asks for a retry: true (one token withdrawn) or false (no whole token left; do not retry).</summary>
    public bool TryRetry()
    {
        lock (_lock)
        {
            // A tiny tolerance, so that ten deposits of 0.1 make one whole token despite binary rounding.
            if (_tokens >= 1 - 1e-9)
            {
                _tokens = Math.Max(0, _tokens - 1);
                _granted++;
                return true;
            }
            _denied++;
            return false;
        }
    }
}

/// <summary>
/// Thrown by strategy D's wait function when the budget refuses a retry. It leaves the article's
/// retry loop unchanged: the loop asks for the wait before every retry, and this exception ends the
/// logical operation there, with the transient failure of attempt <see cref="FailedAttempt"/>.
/// </summary>
public sealed class RetryBudgetExhaustedException(int failedAttempt)
    : Exception($"retry budget exhausted after attempt {failedAttempt}")
{
    public int FailedAttempt => failedAttempt;
}
