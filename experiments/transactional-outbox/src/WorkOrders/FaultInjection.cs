using System.Diagnostics;

namespace WorkOrders;

/// <summary>Failure injection points. Local-lab only; gated by E01_FAULT_INJECTION_ENABLED.</summary>
public enum InjectedFault
{
    None,

    /// <summary>Throw after the INSERTs but before COMMIT, so the transaction rolls back.</summary>
    FailBeforeCommit,

    /// <summary>SIGKILL the process right after COMMIT returns, before anything else runs.</summary>
    CrashAfterCommit,
}

public static class FaultHeader
{
    public const string Name = "X-Fault-Inject";

    public static bool TryParse(string? value, out InjectedFault fault)
    {
        fault = value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "none" => InjectedFault.None,
            "fail-before-commit" => InjectedFault.FailBeforeCommit,
            "crash-after-commit" => InjectedFault.CrashAfterCommit,
            _ => (InjectedFault)(-1),
        };
        return Enum.IsDefined(fault);
    }
}

/// <summary>Raised by the fail-before-commit fault. The transaction has been rolled back.</summary>
public sealed class InjectedFaultException(Guid attemptedWorkOrderId, Guid attemptedEventId)
    : Exception("Injected failure before COMMIT; the transaction was rolled back.")
{
    public Guid AttemptedWorkOrderId { get; } = attemptedWorkOrderId;
    public Guid AttemptedEventId { get; } = attemptedEventId;
}

public interface IProcessTerminator
{
    /// <summary>Never returns.</summary>
    void TerminateNow(string reason);
}

/// <summary>
/// Kills the current process with SIGKILL: no finally blocks, no graceful shutdown, no
/// in-flight work completes. This is the closest in-process approximation of a container
/// OOM-kill or node loss. (In Docker the service runs with `init: true`, so dotnet is not
/// PID 1 and the kernel does not ignore the signal.)
/// </summary>
public sealed class ProcessTerminator(IEnumerable<ILoggerProvider> loggerProviders, ILogger<ProcessTerminator> logger)
    : IProcessTerminator
{
    public void TerminateNow(string reason)
    {
        logger.LogCritical("phase={phase} fault={fault} Killing process with SIGKILL now", "process_killed", reason);

        // The console logger writes on a background thread. Disposing the providers drains that
        // queue so the evidence lines above are not lost with the process. This only flushes logs;
        // it does not change application state.
        foreach (var provider in loggerProviders) provider.Dispose();

        Process.GetCurrentProcess().Kill();
        Thread.Sleep(Timeout.Infinite);
    }
}
