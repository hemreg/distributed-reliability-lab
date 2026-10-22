using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;

namespace WorkOrders.Tests;

/// <summary>
/// Runs the real WorkOrders service as a separate OS process, so the crash-after-commit fault
/// is an actual SIGKILL rather than an exception, and a "restart" is a fresh process.
/// </summary>
public sealed class AppProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly ConcurrentQueue<string> _lines = new();
    private readonly HttpClient _http;

    private AppProcess(Process process, Uri baseAddress)
    {
        _process = process;
        _http = new HttpClient { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(15) };
    }

    public IReadOnlyCollection<string> Logs => _lines;

    public static async Task<AppProcess> StartAsync(IDictionary<string, string> environment)
    {
        var port = FreeTcpPort();
        var psi = new ProcessStartInfo("dotnet")
        {
            ArgumentList = { "exec", Path.Combine(AppContext.BaseDirectory, "WorkOrders.dll") },
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.Environment.Remove("ASPNETCORE_HTTP_PORTS");
        psi.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        foreach (var (key, value) in environment) psi.Environment[key] = value;

        var app = new AppProcess(new Process { StartInfo = psi }, new Uri($"http://127.0.0.1:{port}"));
        app._process.OutputDataReceived += (_, e) => app.Capture(e.Data);
        app._process.ErrorDataReceived += (_, e) => app.Capture(e.Data);
        app._process.Start();
        app._process.BeginOutputReadLine();
        app._process.BeginErrorReadLine();

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (app._process.HasExited)
                throw new InvalidOperationException($"WorkOrders exited during startup:\n{string.Join('\n', app._lines)}");
            try
            {
                if ((await app._http.GetAsync("/health")).StatusCode == HttpStatusCode.OK) return app;
            }
            catch (HttpRequestException) { /* not listening yet */ }
            await Task.Delay(100);
        }
        throw new TimeoutException($"WorkOrders did not become healthy:\n{string.Join('\n', app._lines)}");
    }

    public Task<HttpResponseMessage> CreateWorkOrderAsync(string operationId, string? fault = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/work-orders")
        {
            Content = JsonContent.Create(new { title = $"Work order for {operationId}" }),
        };
        request.Headers.Add("X-Operation-Id", operationId);
        if (fault is not null) request.Headers.Add("X-Fault-Inject", fault);
        return _http.SendAsync(request);
    }

    public async Task WaitForLogAsync(string fragment, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (_lines.Any(l => l.Contains(fragment, StringComparison.Ordinal))) return;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Log fragment '{fragment}' not seen:\n{string.Join('\n', _lines)}");
    }

    public async Task<int> WaitForExitAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        await _process.WaitForExitAsync(cts.Token);
        return _process.ExitCode;
    }

    private void Capture(string? line)
    {
        if (line is null) return;
        _lines.Enqueue(line);
        try { TestContext.Current.TestOutputHelper?.WriteLine($"[app pid={_process.Id}] {line}"); }
        catch (InvalidOperationException) { /* test already finished */ }
    }

    private static int FreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }
        _process.Dispose();
    }
}
