using System.Text.Json;
using Dependency;
using LoadGenerator;

namespace RetryResilience.Tests;

public sealed class FineBucketTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(99.9, 0)]
    [InlineData(100, 1)]
    [InlineData(10_000, 100)]
    [InlineData(10_099.99, 100)]
    [InlineData(-5, 0)]
    [InlineData(10_000_000, ServerRun.MaxFineBuckets - 1)]
    public void Dependency_and_client_use_the_same_100_ms_buckets(double ms, int expected)
    {
        Assert.Equal(expected, ServerRun.FineBucket(ms));
        Assert.Equal(expected, RunRecorder.FineBucket(ms));
    }

    [Fact]
    public void Ten_fine_buckets_add_up_to_their_second()
    {
        var run = new ServerRun("t", FailureMode.None, DependencyOptions.Defaults);
        double[] arrivals = [0, 50, 99.9, 100, 999.9, 1000, 1001, 1950, 2500];
        foreach (var ms in arrivals) run.Received(ms);

        var stats = run.Snapshot(DependencyOptions.Defaults);
        Assert.Equal(100, stats.FineBucketMs);
        var fine = stats.Fine.ToDictionary(f => f.Index, f => f.Received);
        foreach (var second in stats.Seconds)
            Assert.Equal(second.Received, Enumerable.Range(second.T * 10, 10).Sum(i => fine.GetValueOrDefault(i)));
        Assert.Equal(3, fine[0]);   // 0, 50, 99.9
        Assert.Equal(1, fine[1]);   // 100
        Assert.Equal(1, fine[9]);   // 999.9
        Assert.Equal(2, fine[10]);  // 1000, 1001
    }

    [Fact]
    public void Client_counts_attempts_per_100_ms_by_attempt_number_on_the_aligned_clock()
    {
        var recorder = new RunRecorder(1, "B", attemptLog: null);
        recorder.Start(offsetMs: 10_050); // bucket 100 = [10.0, 10.1) s
        recorder.AttemptSent(1);
        recorder.AttemptSent(2);
        recorder.AttemptSent(2);
        recorder.AttemptSent(4);
        recorder.AttemptSent(7); // beyond 4: counted with attempt 4

        Assert.True(recorder.ElapsedMs >= 10_050);
        Assert.Equal(1, recorder.FineAttempts(1)[100]);
        Assert.Equal(2, recorder.FineAttempts(2)[100]);
        Assert.Equal(0, recorder.FineAttempts(3)[100]);
        Assert.Equal(2, recorder.FineAttempts(4)[100]);
        Assert.Equal(5, recorder.AttemptsPerSecond[10]);
        Assert.Equal(4, recorder.RetriesPerSecond[10]);
    }

    [Fact]
    public void Peak_returns_the_highest_bucket_and_its_first_index()
    {
        Assert.Equal((9L, 2), Metrics.Peak([1, 3, 9, 2, 9]));
        Assert.Equal((0L, 0), Metrics.Peak([]));
    }

    [Fact]
    public void SumBuckets_counts_the_buckets_that_start_in_the_range()
    {
        long[] series = [1, 2, 3, 4, 5];               // buckets start at 0, 100, 200, 300, 400 ms
        Assert.Equal(2 + 3 + 4, Metrics.SumBuckets(series, 100, 100, 400));
        Assert.Equal(0, Metrics.SumBuckets(series, 100, 500, 900));
    }
}

public sealed class ThinkTimeTests
{
    [Fact]
    public void Same_seed_and_client_give_the_same_sequence_within_bounds()
    {
        int[] Draw(int seed, int client)
        {
            var t = new ThinkTime(seed, client, 1000, 3000);
            return [.. Enumerable.Range(0, 200).Select(_ => t.NextMs())];
        }

        Assert.Equal(Draw(42, 3), Draw(42, 3));
        Assert.NotEqual(Draw(42, 3), Draw(42, 4));
        Assert.NotEqual(Draw(42, 3), Draw(7, 3));
        Assert.All(Draw(42, 3), ms => Assert.InRange(ms, 1000, 3000));
    }

    [Fact]
    public void First_start_is_spread_over_0_to_max()
    {
        var offsets = Enumerable.Range(0, 2000).Select(c => new ThinkTime(42, c, 1000, 3000).InitialOffsetMs()).ToArray();
        Assert.All(offsets, ms => Assert.InRange(ms, 0, 3000));
        Assert.InRange(offsets.Average(), 1400, 1600);
    }
}

public sealed class RepeatAggregationTests
{
    [Theory]
    [InlineData(new double[] { 5 }, 5)]
    [InlineData(new double[] { 1, 2, 9 }, 2)]
    [InlineData(new double[] { 1, 2, 3, 10 }, 2.5)]
    [InlineData(new double[] { 3, 3, 3, 3, 3 }, 3)]
    public void Median_of_a_sorted_list(double[] sorted, double expected) => Assert.Equal(expected, Repeat.Median(sorted));

    [Fact]
    public void Median_of_an_empty_list_throws() => Assert.Throws<ArgumentException>(() => Repeat.Median([]));

    [Fact]
    public void Spread_ignores_missing_values_and_counts_them()
    {
        var spread = Spread.Of([4, null, 1, 3, 2]);
        Assert.Equal(new Spread(5, 1, 2.5, 1, 4), spread);
        Assert.Equal(new Spread(2, 2, null, null, null), Spread.Of([null, null]));
    }

    [Fact]
    public void Read_follows_a_dotted_path_and_returns_null_for_missing_or_null()
    {
        using var doc = JsonDocument.Parse("""{ "a": { "b": 2.5, "n": null }, "s": "x" }""");
        Assert.Equal(2.5, Repeat.Read(doc.RootElement, "a.b"));
        Assert.Null(Repeat.Read(doc.RootElement, "a.n"));
        Assert.Null(Repeat.Read(doc.RootElement, "a.c"));
        Assert.Null(Repeat.Read(doc.RootElement, "s.t"));
    }

    [Fact]
    public void Collect_reads_every_seed_in_numeric_order_and_aggregates_per_strategy()
    {
        var dir = Directory.CreateTempSubdirectory("e03-repeat-test-").FullName;
        try
        {
            void Write(int seed, double successA, double successB, int? recoveryB)
            {
                var path = Path.Combine(dir, $"seed-{seed}", "e03-b");
                Directory.CreateDirectory(path);
                var recovery = recoveryB is { } r ? r.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null";
                File.WriteAllText(Path.Combine(path, "summary.json"), $$"""
                    { "strategies": [
                        { "id": "A", "operations": { "successPercent": {{successA}} }, "recovery": { "seconds": 0 } },
                        { "id": "B", "operations": { "successPercent": {{successB}} }, "recovery": { "seconds": {{recovery}} } } ] }
                    """);
            }
            Write(10, 90, 99, 1);
            Write(2, 80, 98, null);
            Write(1, 85, 97, 3);
            Directory.CreateDirectory(Path.Combine(dir, "not-a-seed"));

            RepeatMetric[] metrics = [new("Success %", "operations.successPercent", "0.0"), new("Recovery s", "recovery.seconds", "0")];
            var r = Repeat.Collect(dir, "e03-b", metrics)!;

            Assert.Equal([1, 2, 10], r.Seeds);
            Assert.Equal(["A", "B"], r.Strategies);
            Assert.Equal([85.0, 80.0, 90.0], r.Values["A"]["Success %"]);
            Assert.Equal(new Spread(3, 0, 85, 80, 90), r.SpreadOf("A", "Success %"));
            Assert.Equal(new Spread(3, 1, 2, 1, 3), r.SpreadOf("B", "Recovery s"));

            var text = Repeat.Format(r, "E03-B test");
            Assert.Contains("3 runs, seeds 1 2 10", text);
            Assert.Contains("85.0 [80.0-90.0]", text);
            Assert.Contains("2 [1-3] (1 n/a)", text);

            Assert.Null(Repeat.Collect(dir, "e03-a", metrics));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
