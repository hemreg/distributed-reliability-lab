namespace LoadGenerator;

public static class Metrics
{
    /// <summary>Recovery criterion: per-second success ratio of completed logical operations.</summary>
    public const double RecoveryThreshold = 0.95;

    /// <summary>Recovery criterion: consecutive seconds that must meet <see cref="RecoveryThreshold"/>.</summary>
    public const int RecoverySustainSeconds = 3;

    /// <summary>
    /// Nearest-rank percentile: the smallest value such that at least p% of the values are less
    /// than or equal to it (rank = ceil(p/100 * n)). Returns null for an empty list.
    /// </summary>
    public static double? Percentile(IReadOnlyList<double> sortedAscending, double p)
    {
        if (p is <= 0 or > 100) throw new ArgumentOutOfRangeException(nameof(p), p, "must be in (0, 100]");
        var n = sortedAscending.Count;
        if (n == 0) return null;
        var rank = (int)Math.Ceiling(p / 100.0 * n);
        return sortedAscending[Math.Clamp(rank, 1, n) - 1];
    }

    /// <summary>Highest value of a bucketed series and the index of its first occurrence; (0, 0) for an empty series.</summary>
    public static (long Value, int Index) Peak(IReadOnlyList<long> series)
    {
        long best = 0;
        var at = 0;
        for (var i = 0; i < series.Count; i++)
            if (series[i] > best) (best, at) = (series[i], i);
        return (best, at);
    }

    /// <summary>
    /// Sum of the buckets whose start lies in [<paramref name="fromMs"/>, <paramref name="toMs"/>); bucket i starts at
    /// i * <paramref name="bucketMs"/>.
    /// </summary>
    public static long SumBuckets(IReadOnlyList<long> series, int bucketMs, int fromMs, int toMs)
    {
        long sum = 0;
        for (var i = 0; i < series.Count; i++)
        {
            var start = (long)i * bucketMs;
            if (start >= fromMs && start < toMs) sum += series[i];
        }
        return sum;
    }

    /// <summary>
    /// Recovery time after the failure window: the first second s >= <paramref name="windowEndSecond"/>
    /// such that seconds s, s+1, ..., s+sustain-1 each have at least one completed logical operation and
    /// a success ratio (succeeded / (succeeded + failed)) of at least <paramref name="threshold"/>.
    /// Recovery time = s - windowEnd, in whole seconds (0 = the first second after the window already
    /// qualified). Only seconds up to <paramref name="lastSecondInclusive"/> are considered (the arrival
    /// period), so a quiet tail after the load stops cannot count as healthy.
    /// </summary>
    public static Recovery RecoveryAfterWindow(
        IReadOnlyList<long> succeeded, IReadOnlyList<long> failed, int windowEndSecond, int lastSecondInclusive,
        double threshold = RecoveryThreshold, int sustainSeconds = RecoverySustainSeconds)
    {
        bool Healthy(int s)
        {
            var ok = s < succeeded.Count ? succeeded[s] : 0;
            var bad = s < failed.Count ? failed[s] : 0;
            return ok + bad > 0 && ok >= threshold * (ok + bad);
        }

        for (var s = windowEndSecond; s + sustainSeconds - 1 <= lastSecondInclusive; s++)
        {
            var run = 0;
            while (run < sustainSeconds && Healthy(s + run)) run++;
            if (run == sustainSeconds) return new Recovery(true, s - windowEndSecond, s);
        }
        return new Recovery(false, null, null);
    }
}

/// <param name="Recovered">False if the criterion was not met before the load stopped.</param>
/// <param name="Seconds">Whole seconds from the end of the failure window to the first healthy second.</param>
/// <param name="FirstHealthySecond">The first second (since run start) of the healthy streak.</param>
public sealed record Recovery(bool Recovered, int? Seconds, int? FirstHealthySecond);
