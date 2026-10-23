using System.Globalization;

namespace Dependency;

/// <summary>
/// The simulated dependency. All values come from E03_* / E03B_* environment variables (compose.yaml
/// sets them explicitly); the defaults below are the same values.
/// </summary>
/// <remarks>
/// Capacity (workers, service time, queue) is shared by both scenarios. The E03-A failure window
/// (FailureStart/End, FailureServiceTime, FailureErrorRate) and the E03-B correlated failure
/// (Restart, Freeze, Outage, Cold) are separate; each measured run arms exactly one of them.
/// </remarks>
public sealed record DependencyOptions(
    int Workers,
    int ServiceTimeMs,
    int QueueLimit,
    int FailureStartSeconds,
    int FailureEndSeconds,
    int FailureServiceTimeMs,
    double FailureErrorRate,
    int Seed,
    int RestartMs,
    int FreezeMs,
    int OutageMs,
    int ColdMs,
    int ColdServiceTimeMs)
{
    public static readonly DependencyOptions Defaults = new(
        Workers: 10,
        ServiceTimeMs: 40,
        QueueLimit: 100,
        FailureStartSeconds: 10,
        FailureEndSeconds: 20,
        FailureServiceTimeMs: 80,
        FailureErrorRate: 0.5,
        Seed: 42,
        RestartMs: 10_000,
        FreezeMs: 1_500,
        OutageMs: 0,
        ColdMs: 5_000,
        ColdServiceTimeMs: 80);

    /// <summary>Requests per second the workers can complete in normal operation.</summary>
    public double CapacityPerSecond => Workers * 1000.0 / ServiceTimeMs;

    /// <summary>Requests per second the workers can complete inside the failure window.</summary>
    public double FailureCapacityPerSecond => Workers * 1000.0 / FailureServiceTimeMs;

    /// <summary>The E03-B timeline (freeze, restart, outage, cold start) derived from these options.</summary>
    public CorrelatedFailure Correlated => new(RestartMs, FreezeMs, OutageMs, ColdMs, ServiceTimeMs, ColdServiceTimeMs);

    public static DependencyOptions FromConfiguration(IConfiguration configuration)
    {
        int Int(string key, int fallback) =>
            configuration[key] is { Length: > 0 } raw ? int.Parse(raw, CultureInfo.InvariantCulture) : fallback;
        double Double(string key, double fallback) =>
            configuration[key] is { Length: > 0 } raw ? double.Parse(raw, CultureInfo.InvariantCulture) : fallback;

        var d = Defaults;
        var options = new DependencyOptions(
            Workers: Int("E03_WORKERS", d.Workers),
            ServiceTimeMs: Int("E03_SERVICE_TIME_MS", d.ServiceTimeMs),
            QueueLimit: Int("E03_QUEUE_LIMIT", d.QueueLimit),
            FailureStartSeconds: Int("E03_FAILURE_START_S", d.FailureStartSeconds),
            FailureEndSeconds: Int("E03_FAILURE_END_S", d.FailureEndSeconds),
            FailureServiceTimeMs: Int("E03_FAILURE_SERVICE_TIME_MS", d.FailureServiceTimeMs),
            FailureErrorRate: Double("E03_FAILURE_ERROR_RATE", d.FailureErrorRate),
            Seed: Int("E03_SEED", d.Seed),
            RestartMs: Int("E03B_RESTART_MS", d.RestartMs),
            FreezeMs: Int("E03B_FREEZE_MS", d.FreezeMs),
            OutageMs: Int("E03B_OUTAGE_MS", d.OutageMs),
            ColdMs: Int("E03B_COLD_MS", d.ColdMs),
            ColdServiceTimeMs: Int("E03B_COLD_SERVICE_TIME_MS", d.ColdServiceTimeMs));
        options.Validate();
        return options;
    }

    public void Validate()
    {
        if (Workers < 1) throw new ArgumentOutOfRangeException(nameof(Workers), Workers, "must be >= 1");
        if (ServiceTimeMs < 1) throw new ArgumentOutOfRangeException(nameof(ServiceTimeMs), ServiceTimeMs, "must be >= 1");
        if (QueueLimit < 0) throw new ArgumentOutOfRangeException(nameof(QueueLimit), QueueLimit, "must be >= 0");
        if (FailureServiceTimeMs < 1) throw new ArgumentOutOfRangeException(nameof(FailureServiceTimeMs), FailureServiceTimeMs, "must be >= 1");
        if (FailureStartSeconds < 0 || FailureEndSeconds <= FailureStartSeconds)
            throw new ArgumentException($"failure window [{FailureStartSeconds}, {FailureEndSeconds}) s is empty or negative");
        if (FailureErrorRate is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(FailureErrorRate), FailureErrorRate, "must be in [0, 1]");
        Correlated.Validate();
    }
}
