using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace LoadGenerator;

/// <summary>Talks to the dependency's lab-only /admin endpoints (reset, stats, shutdown).</summary>
public sealed class DependencyAdmin(Uri baseAddress) : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http = new() { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>Round trip of the last successful reset, ms (E03-B uses half of it to align the client clock).</summary>
    public double LastResetRoundTripMs { get; private set; }

    public async Task WaitUntilHealthyAsync(TimeSpan limit)
    {
        var until = DateTime.UtcNow + limit;
        while (true)
        {
            try
            {
                using var response = await _http.GetAsync("/health");
                if (response.IsSuccessStatusCode) return;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && DateTime.UtcNow < until) { }
            if (DateTime.UtcNow >= until) throw new TimeoutException($"dependency at {baseAddress} not healthy after {limit.TotalSeconds:0} s");
            await Task.Delay(250);
        }
    }

    /// <summary>
    /// Resets the dependency's counters and clock and arms the failure of <paramref name="scenario"/>
    /// ("a": the E03-A window, "b": the E03-B freeze and restart) unless <paramref name="failure"/> is false.
    /// Waits (up to 60 s) until the dependency is idle first.
    /// </summary>
    public async Task<RunStats> ResetAsync(string runId, bool failure, string scenario = "a")
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (true)
        {
            var sent = System.Diagnostics.Stopwatch.StartNew();
            using var response = await _http.PostAsync(
                $"/admin/reset?runId={Uri.EscapeDataString(runId)}&failure={(failure ? "true" : "false")}&scenario={Uri.EscapeDataString(scenario)}", null);
            if (response.StatusCode != HttpStatusCode.Conflict)
            {
                response.EnsureSuccessStatusCode();
                var stats = (await response.Content.ReadFromJsonAsync<RunStats>(Json))!;
                LastResetRoundTripMs = sent.Elapsed.TotalMilliseconds;
                return stats;
            }
            if (DateTime.UtcNow >= until) throw new TimeoutException("dependency did not become idle within 60 s");
            await Task.Delay(100);
        }
    }

    /// <summary>Waits until no request is inside the dependency (abandoned work included), then reads the run's stats.</summary>
    public async Task<RunStats> IdleStatsAsync()
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (true)
        {
            var stats = (await _http.GetFromJsonAsync<RunStats>("/admin/stats", Json))!;
            if (stats.InSystem == 0 || DateTime.UtcNow >= until) return stats;
            await Task.Delay(100);
        }
    }

    public async Task ShutdownAsync()
    {
        try
        {
            using var _ = await _http.PostAsync("/admin/shutdown", null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Console.Error.WriteLine($"warning: could not stop the dependency ({e.Message}); run `docker compose down`.");
        }
    }

    public void Dispose() => _http.Dispose();
}

// Same shape as the dependency's /admin/stats response.
public sealed record RunStats(
    string RunId, bool FailureEnabled, DateTimeOffset StartedAtUtc, double ElapsedSeconds, int InSystem,
    DependencyConfig Config, DependencyTotals Totals, IReadOnlyList<DependencySecond> Seconds,
    string? Mode = null, DependencyCorrelatedConfig? Correlated = null, DependencyCorrelatedTotals? CorrelatedTotals = null,
    int FineBucketMs = 100, IReadOnlyList<DependencyFine>? Fine = null);

public sealed record DependencyTotals(
    long Received, long Ok, long Injected503, long Rejected503, long Abandoned,
    int PeakInFlight, int PeakQueued, long PeakReceivedPerSecond);

public sealed record DependencySecond(
    int T, long Received, long Ok, long Injected503, long Rejected503, long Abandoned, int PeakInFlight, int PeakQueued);

public sealed record DependencyConfig(
    int Workers, int ServiceTimeMs, int QueueLimit, double CapacityPerSecond,
    int FailureStartSeconds, int FailureEndSeconds, int FailureServiceTimeMs, double FailureErrorRate,
    double FailureCapacityPerSecond, int Seed);

/// <summary>One 100 ms bucket at the dependency: [Index * 100, (Index + 1) * 100) ms since the reset.</summary>
public sealed record DependencyFine(
    int Index, long Received, long Ok, long Injected503, long Rejected503, long Restart503, long Refused503, int PeakInSystem, int PeakQueued);

/// <summary>The E03-B timeline, ms since the reset.</summary>
public sealed record DependencyCorrelatedConfig(
    int FreezeStartMs, int RestartMs, int OutageEndMs, int ColdEndMs,
    int FreezeMs, int OutageMs, int ColdMs, int ColdServiceTimeMs, double ColdCapacityPerSecond);

public sealed record DependencyCorrelatedTotals(long Restart503, long Refused503, double? RestartedAtMs, int HeldAtRestart, int PeakInSystemAfterRestart);
