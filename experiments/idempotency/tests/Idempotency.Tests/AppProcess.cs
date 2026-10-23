using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Idempotency.Tests;

/// <summary>
/// Runs a real E02 program (WorkOrders.dll or Inventory.dll) as a separate OS process, so an
/// injected crash is an actual SIGKILL rather than an exception, and a "restart" is a fresh process.
/// </summary>
public sealed class AppProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly ConcurrentQueue<string> _lines = new();
    private readonly HttpClient _http;
    private readonly string _name;

    private AppProcess(Process process, Uri baseAddress, string name)
    {
        _process = process;
        _name = name;
        _http = new HttpClient { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(15) };
    }

    public IReadOnlyCollection<string> Logs => _lines;

    public static Task<AppProcess> StartWorkerAsync(IDictionary<string, string> environment) =>
        StartAsync("WorkOrders.dll", "outbox-worker", environment);

    public static Task<AppProcess> StartConsumerAsync(string name, IDictionary<string, string> environment) =>
        StartAsync("Inventory.dll", name, environment);

    private static async Task<AppProcess> StartAsync(string dll, string name, IDictionary<string, string> environment)
    {
        var port = FreeTcpPort();
        var psi = new ProcessStartInfo("dotnet")
        {
            ArgumentList = { "exec", Path.Combine(AppContext.BaseDirectory, dll) },
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment.Remove("ASPNETCORE_HTTP_PORTS");
        psi.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        foreach (var (key, value) in environment) psi.Environment[key] = value;

        var app = new AppProcess(new Process { StartInfo = psi }, new Uri($"http://127.0.0.1:{port}"), name);
        app._process.OutputDataReceived += (_, e) => app.Capture(e.Data);
        app._process.ErrorDataReceived += (_, e) => app.Capture(e.Data);
        app._process.Start();
        app._process.BeginOutputReadLine();
        app._process.BeginErrorReadLine();

        // Consumers answer 200 only once they are attached to their queue.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (app._process.HasExited)
                throw new InvalidOperationException($"{name} exited during startup:\n{string.Join('\n', app._lines)}");
            try
            {
                if ((await app._http.GetAsync("/health")).StatusCode == HttpStatusCode.OK) return app;
            }
            catch (HttpRequestException) { /* not listening yet */ }
            await Task.Delay(100);
        }
        throw new TimeoutException($"{name} did not become healthy:\n{string.Join('\n', app._lines)}");
    }

    /// <summary>POST /faults/{fault}?operation_id=...</summary>
    public async Task ArmAsync(string fault, string operationId)
    {
        var response = await _http.PostAsync($"/faults/{fault}?operation_id={Uri.EscapeDataString(operationId)}", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    public async Task<int> WaitForExitAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        await _process.WaitForExitAsync(cts.Token);
        return _process.ExitCode;
    }

    public bool HasExited => _process.HasExited;

    /// <summary>How many JSON log lines of this process have the given phase for the given event_id.</summary>
    public int CountPhase(string phase, Guid eventId) => _lines.Count(l =>
        l.Contains($"\"phase\":\"{phase}\"", StringComparison.Ordinal) &&
        l.Contains($"\"event_id\":\"{eventId}\"", StringComparison.Ordinal));

    private void Capture(string? line)
    {
        if (line is null) return;
        _lines.Enqueue(line);
        try { TestContext.Current.TestOutputHelper?.WriteLine($"[{_name} pid={_process.Id}] {line}"); }
        catch (InvalidOperationException) { /* test already finished */ }
    }

    private static int FreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>Graceful stop (SIGTERM), like `docker compose restart`; SIGKILL if it does not stop in 15 s.</summary>
    public async Task StopGracefullyAsync()
    {
        if (_process.HasExited) return;
        if (SysKill(_process.Id, SigTerm) != 0) throw new InvalidOperationException($"kill -TERM {_process.Id} failed");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await _process.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { _process.Kill(entireProcessTree: true); await _process.WaitForExitAsync(); }
    }

    private const int SigTerm = 15;

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int SysKill(int pid, int signal);

    private bool _disposed;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }
        _process.Dispose();
    }
}
