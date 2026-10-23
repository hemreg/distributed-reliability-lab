using Dependency;
using LoadGenerator;

namespace RetryResilience.Tests;

public sealed class PercentileTests
{
    private static readonly double[] OneToHundred = [.. Enumerable.Range(1, 100).Select(i => (double)i)];

    [Theory]
    [InlineData(50, 50)]
    [InlineData(95, 95)]
    [InlineData(99, 99)]
    [InlineData(100, 100)]
    [InlineData(0.5, 1)]
    public void Nearest_rank_on_1_to_100(double p, double expected) =>
        Assert.Equal(expected, Metrics.Percentile(OneToHundred, p));

    [Fact]
    public void Nearest_rank_returns_an_observed_value_never_an_interpolation()
    {
        double[] sorted = [10, 20, 30, 40];
        Assert.Equal(20, Metrics.Percentile(sorted, 50));  // ceil(0.50 * 4) = 2nd
        Assert.Equal(40, Metrics.Percentile(sorted, 95));  // ceil(0.95 * 4) = 4th
        Assert.Equal(30, Metrics.Percentile(sorted, 51));  // ceil(0.51 * 4) = 3rd
    }

    [Fact]
    public void Single_value_and_empty_list()
    {
        Assert.Equal(7, Metrics.Percentile([7.0], 99));
        Assert.Null(Metrics.Percentile([], 50));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void Rejects_out_of_range_p(double p) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Metrics.Percentile(OneToHundred, p));
}

public sealed class RecoveryTests
{
    // Seconds 0..29; failure window ends at 20; the arrival period ends at 29 (inclusive).
    private static (long[] ok, long[] failed) Series(Func<int, (long ok, long failed)> perSecond)
    {
        var ok = new long[40];
        var failed = new long[40];
        for (var s = 0; s < 30; s++) (ok[s], failed[s]) = perSecond(s);
        return (ok, failed);
    }

    [Fact]
    public void Healthy_right_after_the_window_is_0_seconds()
    {
        var (ok, failed) = Series(s => s is >= 10 and < 20 ? (50, 50) : (100, 0));
        Assert.Equal(new Recovery(true, 0, 20), Metrics.RecoveryAfterWindow(ok, failed, 20, 29));
    }

    [Fact]
    public void Counts_the_seconds_until_a_3_second_healthy_streak_starts()
    {
        // 20, 21 bad; 22 ok; 23 bad; 24, 25, 26 ok -> streak starts at 24 -> 4 s.
        var bad = new HashSet<int> { 20, 21, 23 };
        var (ok, failed) = Series(s => bad.Contains(s) || s is >= 10 and < 20 ? (50, 50) : (100, 0));
        Assert.Equal(new Recovery(true, 4, 24), Metrics.RecoveryAfterWindow(ok, failed, 20, 29));
    }

    [Fact]
    public void Exactly_95_percent_counts_as_healthy_94_9_does_not()
    {
        var (ok, failed) = Series(s => (95, 5));
        Assert.True(Metrics.RecoveryAfterWindow(ok, failed, 20, 29).Recovered);

        var (ok2, failed2) = Series(s => (949, 51));
        Assert.False(Metrics.RecoveryAfterWindow(ok2, failed2, 20, 29).Recovered);
    }

    [Fact]
    public void A_second_without_completions_is_not_healthy()
    {
        var (ok, failed) = Series(s => s == 21 ? (0, 0) : (100, 0));
        Assert.Equal(new Recovery(true, 2, 22), Metrics.RecoveryAfterWindow(ok, failed, 20, 29));
    }

    [Fact]
    public void Not_recovered_when_no_streak_fits_inside_the_arrival_period()
    {
        // Only 28 and 29 are healthy: a 3-second streak would need second 30, after the load stopped.
        var (ok, failed) = Series(s => s >= 28 ? (100, 0) : (10, 90));
        Assert.Equal(new Recovery(false, null, null), Metrics.RecoveryAfterWindow(ok, failed, 20, 29));
    }
}

public sealed class ArrivalScheduleTests
{
    [Fact]
    public void Same_seed_gives_the_same_schedule_different_seed_does_not()
    {
        Assert.Equal(ArrivalSchedule.Poisson(100, 30, 42), ArrivalSchedule.Poisson(100, 30, 42));
        Assert.NotEqual(ArrivalSchedule.Poisson(100, 30, 42), ArrivalSchedule.Poisson(100, 30, 7));
    }

    [Fact]
    public void Arrivals_are_ascending_inside_the_duration_and_near_the_rate()
    {
        var arrivals = ArrivalSchedule.Poisson(100, 30, 42);
        Assert.True(arrivals.Zip(arrivals.Skip(1)).All(p => p.First <= p.Second));
        Assert.All(arrivals, ms => Assert.InRange(ms, 0, 30_000));
        Assert.InRange(arrivals.Length, 2800, 3200); // 3000 expected; Poisson sd is about 55
    }
}

public sealed class CapacityModelTests
{
    [Fact]
    public void Admits_workers_plus_queue_then_rejects_until_someone_leaves()
    {
        var model = new CapacityModel(workers: 2, queueLimit: 1);
        Assert.True(model.TryAdmit(out var n1)); Assert.Equal(1, n1);
        Assert.True(model.TryAdmit(out _));
        Assert.True(model.TryAdmit(out var n3)); Assert.Equal(3, n3);
        Assert.False(model.TryAdmit(out var full)); Assert.Equal(3, full);
        Assert.Equal(3, model.InSystem);

        Assert.Equal(3, model.Leave());
        Assert.True(model.TryAdmit(out _));
    }

    [Fact]
    public async Task Only_workers_requests_are_served_at_a_time_the_rest_wait_in_order()
    {
        var model = new CapacityModel(workers: 1, queueLimit: 2);
        await model.WaitForWorkerAsync();
        var second = model.WaitForWorkerAsync();
        var third = model.WaitForWorkerAsync();
        Assert.False(second.IsCompleted);

        model.ReleaseWorker();
        await second;
        Assert.False(third.IsCompleted);
        model.ReleaseWorker();
        await third;
    }

    [Fact]
    public void Capacity_follows_from_workers_and_service_time()
    {
        var d = DependencyOptions.Defaults;
        Assert.Equal(250, d.CapacityPerSecond);        // 10 workers / 40 ms
        Assert.Equal(125, d.FailureCapacityPerSecond); // 10 workers / 80 ms
    }
}
