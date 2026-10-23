using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Lab.Shared;

public interface IProcessTerminator
{
    /// <summary>Never returns.</summary>
    void TerminateNow(string reason);
}

/// <summary>
/// Kills the current process with SIGKILL: no finally blocks, no graceful shutdown, no
/// in-flight work completes, no open transaction commits, no pending AMQP ack is sent.
/// This is the closest in-process approximation of a container OOM-kill or node loss.
/// (In Docker the services run with `init: true`, so dotnet is not PID 1 and the kernel
/// does not ignore the signal.)
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
