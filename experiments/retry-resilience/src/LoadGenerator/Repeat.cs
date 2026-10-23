using System.Globalization;
using System.Text;
using System.Text.Json;

namespace LoadGenerator;

/// <summary>Median and range of one metric over repeated runs. Missing = runs where the metric had no value.</summary>
public sealed record Spread(int Runs, int Missing, double? Median, double? Min, double? Max)
{
    public static Spread Of(IReadOnlyList<double?> values)
    {
        var present = values.Where(v => v.HasValue).Select(v => v!.Value).Order().ToArray();
        return present.Length == 0
            ? new Spread(values.Count, values.Count, null, null, null)
            : new Spread(values.Count, values.Count - present.Length, Repeat.Median(present), present[0], present[^1]);
    }
}

/// <summary>One metric read from a summary.json strategy object by a dotted camelCase path.</summary>
/// <param name="Format">.NET number format for the console table, e.g. "0.0".</param>
public sealed record RepeatMetric(string Header, string Path, string Format);

/// <summary>One scenario's repeat result: per strategy and metric, the values per seed and their spread.</summary>
public sealed record RepeatScenario(
    string Scenario, IReadOnlyList<int> Seeds, IReadOnlyList<string> Strategies, IReadOnlyList<RepeatMetric> Metrics,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<double?>>> Values)
{
    public Spread SpreadOf(string strategy, string header) => Spread.Of(Values[strategy][header]);
}

/// <summary>
/// <c>LoadGenerator aggregate DIR</c>: reads DIR/seed-N/e03-a/summary.json and DIR/seed-N/e03-b/summary.json
/// (written by ./repeat-e03.sh) and prints, per scenario, strategy and metric, the median and the min-max
/// over the seeds, followed by every individual value. Writes DIR/repeat.txt and DIR/repeat.json.
/// </summary>
public static class Repeat
{
    public static readonly IReadOnlyList<RepeatMetric> MetricsA =
    [
        new("Requests", "attempts.receivedByDependency", "0"),
        new("Ampl.", "attempts.amplificationDependency", "0.00"),
        new("Success %", "operations.successPercent", "0.0"),
        new("In window %", "byArrivalPhase.inWindow.successPercent", "0.0"),
        new("p95 ms", "latencyMs.all.p95", "0"),
        new("p99 ms", "latencyMs.all.p99", "0"),
        new("Peak/s", "dependency.peakReceivedPerSecond", "0"),
        new("Recovery s", "recovery.seconds", "0"),
    ];

    public static readonly IReadOnlyList<RepeatMetric> MetricsB =
    [
        new("Ops", "operations.started", "0"),
        new("Success %", "operations.successPercent", "0.0"),
        new("Affected %", "byStartPhase.affected.successPercent", "0.0"),
        new("p95 ms", "latencyMs.all.p95", "0"),
        new("p99 ms", "latencyMs.all.p99", "0"),
        new("Recovery s", "recovery.seconds", "0"),
        new("Requests", "load.requestsAtDependency", "0"),
        new("Ampl.", "load.amplification", "0.00"),
        new("Retries", "load.retriesSent", "0"),
        new("Denied", "load.retriesDenied", "0"),
        new("Req restart-cold end", "load.requestsDuringRecovery", "0"),
        new("Peak/s", "load.peakRequestsPerSecond", "0"),
        new("Peak/100ms", "load.peakRequestsPer100Ms", "0"),
        new("Active after restart", "concurrency.peakActiveAfterRestart", "0"),
        new("Held at restart", "concurrency.heldAtRestart", "0"),
    ];

    /// <summary>Median of an ascending list: the middle value, or the mean of the two middle values.</summary>
    public static double Median(IReadOnlyList<double> sortedAscending)
    {
        var n = sortedAscending.Count;
        if (n == 0) throw new ArgumentException("empty list", nameof(sortedAscending));
        return n % 2 == 1 ? sortedAscending[n / 2] : (sortedAscending[n / 2 - 1] + sortedAscending[n / 2]) / 2.0;
    }

    /// <summary>Reads a number at a dotted camelCase path; null if a part is missing or the value is null.</summary>
    public static double? Read(JsonElement element, string path)
    {
        foreach (var part in path.Split('.'))
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(part, out element)) return null;
        }
        return element.ValueKind == JsonValueKind.Number ? element.GetDouble() : null;
    }

    /// <summary>Collects one scenario ("e03-a" or "e03-b") from DIR/seed-N/{scenario}/summary.json; null if no run has it.</summary>
    public static RepeatScenario? Collect(string directory, string scenario, IReadOnlyList<RepeatMetric> metrics)
    {
        var runs = new List<(int Seed, JsonDocument Summary)>();
        foreach (var seedDirectory in Directory.Exists(directory) ? Directory.GetDirectories(directory, "seed-*") : [])
        {
            var name = Path.GetFileName(seedDirectory);
            if (!int.TryParse(name["seed-".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seed)) continue;
            var file = Path.Combine(seedDirectory, scenario, "summary.json");
            if (File.Exists(file)) runs.Add((seed, JsonDocument.Parse(File.ReadAllText(file))));
        }
        if (runs.Count == 0) return null;
        runs.Sort((x, y) => x.Seed.CompareTo(y.Seed));

        try
        {
            var strategies = new List<string>();
            var values = new Dictionary<string, Dictionary<string, List<double?>>>();
            foreach (var (_, summary) in runs)
                foreach (var s in summary.RootElement.GetProperty("strategies").EnumerateArray())
                {
                    var id = s.GetProperty("id").GetString()!;
                    if (!values.ContainsKey(id))
                    {
                        strategies.Add(id);
                        values[id] = metrics.ToDictionary(m => m.Header, _ => new List<double?>());
                    }
                }

            foreach (var (_, summary) in runs)
            {
                var byId = summary.RootElement.GetProperty("strategies").EnumerateArray()
                    .ToDictionary(s => s.GetProperty("id").GetString()!);
                foreach (var id in strategies)
                    foreach (var m in metrics)
                        values[id][m.Header].Add(byId.TryGetValue(id, out var s) ? Read(s, m.Path) : null);
            }

            return new RepeatScenario(scenario, [.. runs.Select(r => r.Seed)], strategies, metrics,
                values.ToDictionary(kv => kv.Key,
                    kv => (IReadOnlyDictionary<string, IReadOnlyList<double?>>)kv.Value.ToDictionary(m => m.Key, m => (IReadOnlyList<double?>)m.Value)));
        }
        finally
        {
            foreach (var (_, summary) in runs) summary.Dispose();
        }
    }

    public static string Format(RepeatScenario r, string title)
    {
        var ic = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        const int column = 24;
        var labelWidth = r.Metrics.Max(m => m.Header.Length) + 2;
        var width = labelWidth + column * r.Strategies.Count;
        sb.AppendLine(new string('=', width));
        sb.AppendLine(string.Create(ic, $"{title}: {r.Seeds.Count} runs, seeds {string.Join(' ', r.Seeds)}. Median [min-max] per strategy."));
        sb.AppendLine(new string('=', width));
        sb.Append("Metric".PadRight(labelWidth));
        foreach (var id in r.Strategies) sb.Append(id.PadLeft(column));
        sb.AppendLine();
        sb.AppendLine(new string('-', width));
        foreach (var m in r.Metrics)
        {
            sb.Append(m.Header.PadRight(labelWidth));
            foreach (var id in r.Strategies)
            {
                var spread = r.SpreadOf(id, m.Header);
                var cell = spread.Median is null
                    ? "n/a"
                    : string.Create(ic, $"{spread.Median.Value.ToString(m.Format, ic)} [{spread.Min!.Value.ToString(m.Format, ic)}-{spread.Max!.Value.ToString(m.Format, ic)}]")
                      + (spread.Missing > 0 ? string.Create(ic, $" ({spread.Missing} n/a)") : "");
                sb.Append(cell.PadLeft(column));
            }
            sb.AppendLine();
        }
        sb.AppendLine(new string('-', width));
        sb.AppendLine("Every value, in seed order (" + string.Join(' ', r.Seeds) + "):");
        foreach (var m in r.Metrics)
            foreach (var id in r.Strategies)
            {
                var values = r.Values[id][m.Header].Select(v => v is { } x ? x.ToString(m.Format, ic) : "n/a");
                sb.AppendLine($"  {m.Header,-22} {id}: {string.Join("  ", values)}");
            }
        return sb.ToString();
    }

    public static async Task<int> RunAsync(string directory)
    {
        var scenarios = new[]
        {
            Collect(directory, "e03-a", MetricsA) is { } a ? (a, "E03-A Transient failure under independent arrivals") : default,
            Collect(directory, "e03-b", MetricsB) is { } b ? (b, "E03-B " + CorrelatedReport.Title) : default,
        }.Where(s => s.Item1 is not null).ToArray();

        if (scenarios.Length == 0)
        {
            Console.Error.WriteLine($"no runs found under {directory} (expected seed-N/e03-a/summary.json or seed-N/e03-b/summary.json)");
            return 1;
        }

        var text = new StringBuilder();
        foreach (var (scenario, title) in scenarios)
        {
            text.Append(Format(scenario!, title));
            text.AppendLine();
        }
        text.AppendLine("Each run is one `docker compose up` on one machine; ranges show seed-to-seed (and run-to-run) variation, not a confidence interval.");
        Console.Write(text.ToString());

        await File.WriteAllTextAsync(Path.Combine(directory, "repeat.txt"), text.ToString());
        await Report.WriteJsonAsync(Path.Combine(directory, "repeat.json"), new
        {
            schema = "drlab-e03/repeat/v1",
            scenarios = scenarios.Select(s => new
            {
                scenario = s.Item1!.Scenario,
                title = s.Item2,
                seeds = s.Item1.Seeds,
                strategies = s.Item1.Strategies.Select(id => new
                {
                    id,
                    metrics = s.Item1.Metrics.Select(m => new
                    {
                        metric = m.Header,
                        path = m.Path,
                        values = s.Item1.Values[id][m.Header],
                        spread = s.Item1.SpreadOf(id, m.Header),
                    }),
                }),
            }),
        });
        return 0;
    }
}
