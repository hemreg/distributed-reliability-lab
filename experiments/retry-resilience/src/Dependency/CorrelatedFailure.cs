namespace Dependency;

/// <summary>Where an E03-B run is on its failure timeline at a given moment.</summary>
public enum CorrelatedPhase
{
    /// <summary>Before the freeze: normal capacity.</summary>
    Normal,

    /// <summary>The process hangs: nothing completes, every request (in service, queued or new) is held.</summary>
    Frozen,

    /// <summary>Optional, after the restart: every request is refused at once (503), nothing is admitted.</summary>
    Outage,

    /// <summary>After the restart (and outage): a fresh process with reduced capacity (cold start).</summary>
    Cold,

    /// <summary>After the cold start: normal capacity again.</summary>
    Recovered,
}

/// <summary>
/// E03-B "correlated failure": the dependency freezes for <see cref="FreezeMs"/>, then restarts at
/// <see cref="RestartMs"/>. Every request that is inside at that instant fails at the same moment
/// (503), so the clients' retries all start from the same instant. The restarted process may refuse
/// requests for <see cref="OutageMs"/>, then serves with the slower <see cref="ColdServiceTimeMs"/>
/// for <see cref="ColdMs"/> before it is back to <see cref="ServiceTimeMs"/>.
/// </summary>
/// <remarks>
/// All times are milliseconds since the run's reset. The freeze must be shorter than the client's
/// 2 s attempt timeout, otherwise the held requests time out one by one (spread out in time) instead
/// of failing together at the restart.
/// </remarks>
public sealed record CorrelatedFailure(int RestartMs, int FreezeMs, int OutageMs, int ColdMs, int ServiceTimeMs, int ColdServiceTimeMs)
{
    public int FreezeStartMs => RestartMs - FreezeMs;
    public int OutageEndMs => RestartMs + OutageMs;
    public int ColdEndMs => OutageEndMs + ColdMs;

    public CorrelatedPhase PhaseAt(double ms) =>
        ms < FreezeStartMs ? CorrelatedPhase.Normal
        : ms < RestartMs ? CorrelatedPhase.Frozen
        : ms < OutageEndMs ? CorrelatedPhase.Outage
        : ms < ColdEndMs ? CorrelatedPhase.Cold
        : CorrelatedPhase.Recovered;

    /// <summary>Service time of a request whose service starts at <paramref name="ms"/>.</summary>
    public int ServiceTimeAt(double ms) => PhaseAt(ms) == CorrelatedPhase.Cold ? ColdServiceTimeMs : ServiceTimeMs;

    public void Validate()
    {
        if (FreezeMs < 0) throw new ArgumentOutOfRangeException(nameof(FreezeMs), FreezeMs, "must be >= 0");
        if (FreezeStartMs < 0) throw new ArgumentException($"freeze ({FreezeMs} ms) starts before the run ({RestartMs} ms restart)");
        if (OutageMs < 0) throw new ArgumentOutOfRangeException(nameof(OutageMs), OutageMs, "must be >= 0");
        if (ColdMs < 0) throw new ArgumentOutOfRangeException(nameof(ColdMs), ColdMs, "must be >= 0");
        if (ColdServiceTimeMs < 1) throw new ArgumentOutOfRangeException(nameof(ColdServiceTimeMs), ColdServiceTimeMs, "must be >= 1");
    }
}
