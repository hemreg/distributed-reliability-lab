using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace LoadGenerator;

/// <summary>
/// Client-side measurements of one strategy run, bucketed per second and per 100 ms since the run started.
/// </summary>
/// <param name="operations">Expected number of logical operations (open loop) or a capacity hint (closed loop).</param>
public sealed class RunRecorder(int operations, string strategyId, TextWriter? attemptLog)
{
    public const int MaxSeconds = 600;
    public const int FineBucketMs = 100;
    public const int MaxFineBuckets = MaxSeconds * 1000 / FineBucketMs;

    /// <summary>Attempts are counted per attempt number 1..4 in the fine series (anything higher in the last).</summary>
    public const int AttemptNumbers = RetryLoop.MaxAttempts;

    private readonly Stopwatch _clock = new();
    private readonly long[] _attempts = new long[MaxSeconds];
    private readonly long[] _retries = new long[MaxSeconds];
    private readonly long[] _succeeded = new long[MaxSeconds];
    private readonly long[] _failed = new long[MaxSeconds];
    private readonly long[] _denied = new long[MaxSeconds];
    private readonly long[][] _fineAttemptsByNumber = [.. Enumerable.Range(0, AttemptNumbers).Select(_ => new long[MaxFineBuckets])];
    private readonly long[] _fineSucceeded = new long[MaxFineBuckets];
    private readonly long[] _fineFailed = new long[MaxFineBuckets];
    private readonly long[] _fineDenied = new long[MaxFineBuckets];
    private readonly ConcurrentDictionary<int, long> _responses = new();
    private readonly List<OperationResult> _results = new(operations);
    private readonly Lock _resultsLock = new();
    private readonly Lock _logLock = new();
    private long _attemptsSent, _timeouts, _deadlineCut, _httpErrors, _retriesDenied;

    /// <summary>Completed logical operations, ordered by operation index.</summary>
    public IReadOnlyList<OperationResult> Results
    {
        get
        {
            lock (_resultsLock) return [.. _results.OrderBy(r => r.Index)];
        }
    }

    private double _offsetMs;

    /// <param name="offsetMs">
    /// Where the dependency's run clock already stands when this clock starts (E03-B: so that client-side
    /// and dependency-side buckets line up around the restart; E03-A: 0, as before).
    /// </param>
    public void Start(double offsetMs = 0)
    {
        _offsetMs = offsetMs;
        _clock.Start();
    }

    public double ElapsedMs => _clock.Elapsed.TotalMilliseconds + _offsetMs;

    public static int Bucket(double ms) => Math.Clamp((int)(ms / 1000.0), 0, MaxSeconds - 1);
    public static int FineBucket(double ms) => Math.Clamp((int)(ms / FineBucketMs), 0, MaxFineBuckets - 1);

    public long AttemptsSent => Interlocked.Read(ref _attemptsSent);
    public long Timeouts => Interlocked.Read(ref _timeouts);
    public long DeadlineCut => Interlocked.Read(ref _deadlineCut);
    public long HttpErrors => Interlocked.Read(ref _httpErrors);
    public long RetriesDenied => Interlocked.Read(ref _retriesDenied);
    public IReadOnlyDictionary<int, long> Responses => _responses;
    public IReadOnlyList<long> AttemptsPerSecond => _attempts;
    public IReadOnlyList<long> RetriesPerSecond => _retries;
    public IReadOnlyList<long> SucceededPerSecond => _succeeded;
    public IReadOnlyList<long> FailedPerSecond => _failed;
    public IReadOnlyList<long> RetriesDeniedPerSecond => _denied;

    /// <summary>Attempts sent per 100 ms with attempt number <paramref name="attempt"/> (1-based).</summary>
    public IReadOnlyList<long> FineAttempts(int attempt) => _fineAttemptsByNumber[Math.Clamp(attempt, 1, AttemptNumbers) - 1];
    public IReadOnlyList<long> FineSucceeded => _fineSucceeded;
    public IReadOnlyList<long> FineFailed => _fineFailed;
    public IReadOnlyList<long> FineRetriesDenied => _fineDenied;

    /// <param name="attempt">1 for the first attempt of a logical operation, 2..4 for retries; 0 if unknown.</param>
    public double AttemptSent(int attempt = 0)
    {
        var now = ElapsedMs;
        Interlocked.Increment(ref _attempts[Bucket(now)]);
        if (attempt >= 2) Interlocked.Increment(ref _retries[Bucket(now)]);
        if (attempt >= 1) Interlocked.Increment(ref _fineAttemptsByNumber[Math.Min(attempt, AttemptNumbers) - 1][FineBucket(now)]);
        Interlocked.Increment(ref _attemptsSent);
        return now;
    }

    /// <summary>Strategy D: the budget refused a retry.</summary>
    public void RetryDenied()
    {
        var now = ElapsedMs;
        Interlocked.Increment(ref _denied[Bucket(now)]);
        Interlocked.Increment(ref _fineDenied[FineBucket(now)]);
        Interlocked.Increment(ref _retriesDenied);
    }

    public void AttemptAnswered(OperationContext? op, int attempt, double sentMs, HttpStatusCode status)
    {
        _responses.AddOrUpdate((int)status, 1, (_, n) => n + 1);
        Log(op, attempt, sentMs, ((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public void AttemptTimedOut(OperationContext? op, int attempt, double sentMs)
    {
        Interlocked.Increment(ref _timeouts);
        Log(op, attempt, sentMs, "timeout");
    }

    public void AttemptCutByDeadline(OperationContext? op, int attempt, double sentMs)
    {
        Interlocked.Increment(ref _deadlineCut);
        Log(op, attempt, sentMs, "deadline");
    }

    public void AttemptFailed(OperationContext? op, int attempt, double sentMs, HttpRequestException error)
    {
        Interlocked.Increment(ref _httpErrors);
        Log(op, attempt, sentMs, $"http_error:{error.HttpRequestError}");
    }

    public void Completed(OperationResult result)
    {
        lock (_resultsLock) _results.Add(result);
        var end = result.ScheduledMs + result.LatencyMs;
        Interlocked.Increment(ref (result.Succeeded ? _succeeded : _failed)[Bucket(end)]);
        Interlocked.Increment(ref (result.Succeeded ? _fineSucceeded : _fineFailed)[FineBucket(end)]);
    }

    private void Log(OperationContext? op, int attempt, double sentMs, string outcome)
    {
        if (attemptLog is null || op is null) return;
        var line = JsonSerializer.Serialize(new
        {
            strategy = strategyId,
            operation_id = op.Index,
            attempt,
            phase = "attempt_completed",
            sent_ms = Math.Round(sentMs, 1),
            duration_ms = Math.Round(ElapsedMs - sentMs, 1),
            outcome,
        });
        lock (_logLock) attemptLog.WriteLine(line);
    }
}

/// <param name="Outcome">succeeded | exhausted_503 | exhausted_timeout | exhausted_http_error | deadline_exceeded | retry_budget_exhausted | status_NNN</param>
/// <param name="ScheduledMs">Scheduled arrival (open loop) or start (closed loop); latency is measured from here.</param>
public sealed record OperationResult(int Index, double ScheduledMs, double LatencyMs, bool Succeeded, string Outcome, int Attempts);

/// <summary>Per-operation state that flows (AsyncLocal) from the operation into the HTTP handler.</summary>
public sealed class OperationContext(int index, CancellationToken deadline)
{
    private static readonly AsyncLocal<OperationContext?> Slot = new();

    public static OperationContext? Current
    {
        get => Slot.Value;
        set => Slot.Value = value;
    }

    public int Index => index;
    public CancellationToken Deadline => deadline;
    public int Attempts { get; set; }
}

/// <summary>
/// Counts every attempt the retry loop sends and how it ended, without touching the loop itself.
/// The body is read here, so the whole exchange counts toward the attempt (and its 2 s timeout).
/// </summary>
public sealed class InstrumentedHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    public RunRecorder? Recorder { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var recorder = Recorder;
        var op = OperationContext.Current;
        var attempt = 0;
        if (op is not null)
        {
            attempt = ++op.Attempts;
            request.Headers.TryAddWithoutValidation("X-Operation-Id", op.Index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            request.Headers.TryAddWithoutValidation("X-Attempt", attempt.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        var sentMs = recorder?.AttemptSent(attempt) ?? 0;

        HttpResponseMessage? response = null;
        try
        {
            response = await base.SendAsync(request, cancellationToken);
            await response.Content.LoadIntoBufferAsync(cancellationToken);
            recorder?.AttemptAnswered(op, attempt, sentMs, response.StatusCode);
            return response;
        }
        catch (OperationCanceledException) when (op is { Deadline.IsCancellationRequested: true })
        {
            response?.Dispose();
            recorder?.AttemptCutByDeadline(op, attempt, sentMs);
            throw;
        }
        catch (OperationCanceledException)
        {
            response?.Dispose();
            recorder?.AttemptTimedOut(op, attempt, sentMs);
            throw;
        }
        catch (HttpRequestException e)
        {
            response?.Dispose();
            recorder?.AttemptFailed(op, attempt, sentMs, e);
            throw;
        }
    }
}
