using System.Diagnostics;

namespace Dependency;

/// <summary>Which failure a measured run arms.</summary>
public enum FailureMode
{
    /// <summary>Warm-up and probe runs: no failure.</summary>
    None,

    /// <summary>E03-A: a window of higher latency and random injected 503s.</summary>
    Window,

    /// <summary>E03-B: freeze, then a restart that fails every request inside at the same instant, then a cold start.</summary>
    CorrelatedRestart,
}

/// <summary>
/// Counters and clock of one measured run, started by <c>POST /admin/reset</c>.
/// Everything is bucketed per second and per 100 ms since the reset, measured here at the dependency.
/// </summary>
/// <remarks>
/// Each run owns its capacity model. An E03-B run owns two: the process before the restart and the
/// fresh process after it (new workers, empty queue), so requests torn down by the restart never
/// occupy the restarted process's admission limit.
/// </remarks>
public sealed class ServerRun
{
    public const int MaxSeconds = 600;
    public const int BucketsPerSecond = 10;
    public const int FineBucketMs = 1000 / BucketsPerSecond;
    public const int MaxFineBuckets = MaxSeconds * BucketsPerSecond;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Random _random;
    private readonly Lock _randomLock = new();
    private readonly CancellationTokenSource _restart = new();

    // Per second (the E03-A report and the per-second time series).
    private readonly long[] _received = new long[MaxSeconds];
    private readonly long[] _ok = new long[MaxSeconds];
    private readonly long[] _injected = new long[MaxSeconds];
    private readonly long[] _rejected = new long[MaxSeconds];
    private readonly long[] _abandoned = new long[MaxSeconds];
    private readonly long[] _restartFailed = new long[MaxSeconds];
    private readonly long[] _refused = new long[MaxSeconds];
    private readonly int[] _peakInSystem = new int[MaxSeconds];
    private readonly int[] _peakQueued = new int[MaxSeconds];

    // Per 100 ms (E03-B: synchronised retry waves are shorter than a second).
    private readonly long[] _fineReceived = new long[MaxFineBuckets];
    private readonly long[] _fineOk = new long[MaxFineBuckets];
    private readonly long[] _fineRejected = new long[MaxFineBuckets];
    private readonly long[] _fineRestartFailed = new long[MaxFineBuckets];
    private readonly long[] _fineRefused = new long[MaxFineBuckets];
    private readonly long[] _fineInjected = new long[MaxFineBuckets];
    private readonly int[] _finePeakInSystem = new int[MaxFineBuckets];
    private readonly int[] _finePeakQueued = new int[MaxFineBuckets];

    private int _peakInSystemAfterRestart;
    private double _restartedAtMs = -1;
    private int _heldAtRestart;

    public ServerRun(string runId, FailureMode mode, DependencyOptions options)
    {
        RunId = runId;
        Mode = mode;
        StartedAtUtc = DateTimeOffset.UtcNow;
        Correlated = options.Correlated;
        _random = new Random(options.Seed);
        Model = new CapacityModel(options.Workers, options.QueueLimit);
        ModelAfterRestart = mode == FailureMode.CorrelatedRestart ? new CapacityModel(options.Workers, options.QueueLimit) : Model;
    }

    public string RunId { get; }
    public FailureMode Mode { get; }
    public bool FailureEnabled => Mode != FailureMode.None;
    public DateTimeOffset StartedAtUtc { get; }
    public CorrelatedFailure Correlated { get; }
    public double ElapsedSeconds => _clock.Elapsed.TotalSeconds;
    public double ElapsedMs => _clock.Elapsed.TotalMilliseconds;

    /// <summary>The process (E03-A, warm-up: the only one; E03-B: the one before the restart).</summary>
    public CapacityModel Model { get; }

    /// <summary>E03-B: the restarted process. Other modes: the same object as <see cref="Model"/>.</summary>
    public CapacityModel ModelAfterRestart { get; }

    /// <summary>Requests inside the dependency right now, across both processes of an E03-B run.</summary>
    public int InSystem => ReferenceEquals(Model, ModelAfterRestart) ? Model.InSystem : Model.InSystem + ModelAfterRestart.InSystem;

    /// <summary>Cancelled at the E03-B restart: every request of the old process fails at that instant.</summary>
    public CancellationToken RestartToken => _restart.Token;
    public bool HasRestarted => _restart.IsCancellationRequested;
    public double RestartedAtMs => Volatile.Read(ref _restartedAtMs);

    public static int Second(double ms) => Math.Clamp((int)(ms / 1000.0), 0, MaxSeconds - 1);
    public static int FineBucket(double ms) => Math.Clamp((int)(ms / FineBucketMs), 0, MaxFineBuckets - 1);

    public bool InFailureWindow(DependencyOptions options)
    {
        if (Mode != FailureMode.Window) return false;
        var t = ElapsedSeconds;
        return t >= options.FailureStartSeconds && t < options.FailureEndSeconds;
    }

    /// <summary>E03-B: the restart. Fails every request of the old process at once.</summary>
    public void Restart()
    {
        if (_restart.IsCancellationRequested) return;
        Volatile.Write(ref _restartedAtMs, ElapsedMs);
        Volatile.Write(ref _heldAtRestart, Model.InSystem);
        _restart.Cancel();
    }

    public double NextDouble()
    {
        lock (_randomLock) return _random.NextDouble();
    }

    public void Received(double ms) => Count(_received, _fineReceived, ms);
    public void Ok(double ms) => Count(_ok, _fineOk, ms);
    public void Injected(double ms) => Count(_injected, _fineInjected, ms);
    public void Rejected(double ms) => Count(_rejected, _fineRejected, ms);
    public void RestartFailed(double ms) => Count(_restartFailed, _fineRestartFailed, ms);
    public void Refused(double ms) => Count(_refused, _fineRefused, ms);
    public void Abandoned(double ms) => Interlocked.Increment(ref _abandoned[Second(ms)]);

    private static void Count(long[] perSecond, long[] fine, double ms)
    {
        Interlocked.Increment(ref perSecond[Second(ms)]);
        Interlocked.Increment(ref fine[FineBucket(ms)]);
    }

    /// <summary>Records the in-system and queued counts observed at an admission or a departure.</summary>
    public void Observe(double ms, int inSystem, int queued)
    {
        Max(ref _peakInSystem[Second(ms)], inSystem);
        Max(ref _peakQueued[Second(ms)], queued);
        Max(ref _finePeakInSystem[FineBucket(ms)], inSystem);
        Max(ref _finePeakQueued[FineBucket(ms)], queued);
    }

    /// <summary>E03-B: records the restarted process's own in-system count (requests torn down by the restart excluded).</summary>
    public void ObserveRestarted(int inSystem) => Max(ref _peakInSystemAfterRestart, inSystem);

    private static void Max(ref int slot, int value)
    {
        var current = Volatile.Read(ref slot);
        while (value > current)
        {
            var seen = Interlocked.CompareExchange(ref slot, value, current);
            if (seen == current) return;
            current = seen;
        }
    }

    public RunStats Snapshot(DependencyOptions options)
    {
        var last = -1;
        for (var s = 0; s < MaxSeconds; s++)
            if (_received[s] + _ok[s] + _injected[s] + _rejected[s] + _restartFailed[s] + _refused[s] + _peakInSystem[s] > 0) last = s;

        var seconds = new List<SecondStats>(last + 1);
        for (var s = 0; s <= last; s++)
            seconds.Add(new SecondStats(s, _received[s], _ok[s], _injected[s], _rejected[s], _abandoned[s], _peakInSystem[s], _peakQueued[s]));

        var lastFine = -1;
        for (var b = 0; b < MaxFineBuckets; b++)
            if (_fineReceived[b] + _fineOk[b] + _fineInjected[b] + _fineRejected[b] + _fineRestartFailed[b] + _fineRefused[b] + _finePeakInSystem[b] > 0) lastFine = b;

        var fine = new List<FineStats>(lastFine + 1);
        for (var b = 0; b <= lastFine; b++)
            fine.Add(new FineStats(b, _fineReceived[b], _fineOk[b], _fineInjected[b], _fineRejected[b], _fineRestartFailed[b], _fineRefused[b],
                _finePeakInSystem[b], _finePeakQueued[b]));

        var correlated = Mode == FailureMode.CorrelatedRestart;
        return new RunStats(
            RunId, FailureEnabled, StartedAtUtc, Math.Round(ElapsedSeconds, 3), InSystem,
            Config.From(options),
            new Totals(
                Received: _received.Sum(),
                Ok: _ok.Sum(),
                Injected503: _injected.Sum(),
                Rejected503: _rejected.Sum(),
                Abandoned: _abandoned.Sum(),
                PeakInFlight: _peakInSystem.Max(),
                PeakQueued: _peakQueued.Max(),
                PeakReceivedPerSecond: _received.Max()),
            seconds,
            Mode.ToString(),
            CorrelatedConfig.From(options),
            correlated
                ? new CorrelatedTotals(
                    Restart503: _restartFailed.Sum(),
                    Refused503: _refused.Sum(),
                    RestartedAtMs: RestartedAtMs < 0 ? null : Math.Round(RestartedAtMs, 1),
                    HeldAtRestart: Volatile.Read(ref _heldAtRestart),
                    PeakInSystemAfterRestart: Volatile.Read(ref _peakInSystemAfterRestart))
                : null,
            FineBucketMs,
            fine);
    }
}

/// <param name="Mode">None, Window (E03-A) or CorrelatedRestart (E03-B).</param>
/// <param name="Correlated">E03-B timeline (configuration; reported in every mode, armed only in CorrelatedRestart).</param>
/// <param name="CorrelatedTotals">E03-B totals; null for other modes.</param>
/// <param name="FineBucketMs">Width of the <paramref name="Fine"/> buckets (100 ms).</param>
public sealed record RunStats(
    string RunId, bool FailureEnabled, DateTimeOffset StartedAtUtc, double ElapsedSeconds, int InSystem,
    Config Config, Totals Totals, IReadOnlyList<SecondStats> Seconds,
    string Mode, CorrelatedConfig? Correlated, CorrelatedTotals? CorrelatedTotals, int FineBucketMs, IReadOnlyList<FineStats> Fine);

public sealed record Totals(
    long Received, long Ok, long Injected503, long Rejected503, long Abandoned,
    int PeakInFlight, int PeakQueued, long PeakReceivedPerSecond);

/// <param name="T">Second since the reset (bucket [T, T+1)).</param>
/// <param name="Received">Requests that arrived in this second (admitted or rejected).</param>
/// <param name="Ok">200 responses completed in this second.</param>
/// <param name="Injected503">Injected transient failures (503 after doing the work) completed in this second.</param>
/// <param name="Rejected503">Requests rejected at once because workers and queue were full (503).</param>
/// <param name="Abandoned">Requests completed in this second whose client had already gone (timed out).</param>
/// <param name="PeakInFlight">Highest number of requests inside the dependency (in service + queued) seen in this second.</param>
/// <param name="PeakQueued">Highest number of requests waiting for a worker seen in this second.</param>
public sealed record SecondStats(
    int T, long Received, long Ok, long Injected503, long Rejected503, long Abandoned, int PeakInFlight, int PeakQueued);

/// <param name="Index">Bucket number: the bucket covers [Index * 100, (Index + 1) * 100) ms since the reset.</param>
/// <param name="Restart503">Requests of the old process failed by the E03-B restart in this bucket.</param>
/// <param name="Refused503">Requests refused at once during the E03-B outage (after the restart).</param>
/// <param name="PeakInSystem">Highest number of requests inside the dependency (both processes) seen in this bucket.</param>
public sealed record FineStats(
    int Index, long Received, long Ok, long Injected503, long Rejected503, long Restart503, long Refused503, int PeakInSystem, int PeakQueued);

public sealed record Config(
    int Workers, int ServiceTimeMs, int QueueLimit, double CapacityPerSecond,
    int FailureStartSeconds, int FailureEndSeconds, int FailureServiceTimeMs, double FailureErrorRate,
    double FailureCapacityPerSecond, int Seed)
{
    public static Config From(DependencyOptions o) => new(
        o.Workers, o.ServiceTimeMs, o.QueueLimit, o.CapacityPerSecond,
        o.FailureStartSeconds, o.FailureEndSeconds, o.FailureServiceTimeMs, o.FailureErrorRate,
        o.FailureCapacityPerSecond, o.Seed);
}

/// <summary>The E03-B timeline, milliseconds since the reset.</summary>
public sealed record CorrelatedConfig(
    int FreezeStartMs, int RestartMs, int OutageEndMs, int ColdEndMs,
    int FreezeMs, int OutageMs, int ColdMs, int ColdServiceTimeMs, double ColdCapacityPerSecond)
{
    public static CorrelatedConfig From(DependencyOptions o)
    {
        var c = o.Correlated;
        return new(c.FreezeStartMs, c.RestartMs, c.OutageEndMs, c.ColdEndMs, c.FreezeMs, c.OutageMs, c.ColdMs,
            c.ColdServiceTimeMs, o.Workers * 1000.0 / c.ColdServiceTimeMs);
    }
}

/// <param name="RestartedAtMs">When the restart actually happened (timer), ms since the reset.</param>
/// <param name="HeldAtRestart">Requests inside the old process at the restart: all of them failed at that instant.</param>
/// <param name="PeakInSystemAfterRestart">Highest in-system count of the restarted process (in service + queued).</param>
public sealed record CorrelatedTotals(long Restart503, long Refused503, double? RestartedAtMs, int HeldAtRestart, int PeakInSystemAfterRestart);
