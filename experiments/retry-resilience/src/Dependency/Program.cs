using Dependency;
using Microsoft.Extensions.Logging.Console;

// E03 dependency: a GET endpoint with finite capacity and, per measured run, one scheduled failure:
// E03-A a window of higher latency and injected 503s, E03-B a freeze followed by a restart.
// LOCAL LAB ONLY: the /admin endpoints reset counters and stop the process without authentication.

var builder = WebApplication.CreateBuilder(args);
var options = DependencyOptions.FromConfiguration(builder.Configuration);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.FormatterName = CompactJsonConsoleFormatter.FormatterName);
builder.Logging.AddConsoleFormatter<CompactJsonConsoleFormatter, ConsoleFormatterOptions>();
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);

var app = builder.Build();
var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Dependency");

// Until the first reset there is no failure.
var current = new ServerRun("startup", FailureMode.None, options);
var correlated = options.Correlated;

log.LogInformation(
    "phase={phase} workers={workers} service_time_ms={service_time_ms} queue_limit={queue_limit} capacity_rps={capacity_rps} " +
    "failure_start_s={failure_start_s} failure_end_s={failure_end_s} failure_service_time_ms={failure_service_time_ms} " +
    "failure_error_rate={failure_error_rate} failure_capacity_rps={failure_capacity_rps} " +
    "e03b_freeze_start_ms={e03b_freeze_start_ms} e03b_restart_ms={e03b_restart_ms} e03b_outage_end_ms={e03b_outage_end_ms} " +
    "e03b_cold_end_ms={e03b_cold_end_ms} e03b_cold_service_time_ms={e03b_cold_service_time_ms} Dependency started",
    "startup", options.Workers, options.ServiceTimeMs, options.QueueLimit, options.CapacityPerSecond,
    options.FailureStartSeconds, options.FailureEndSeconds, options.FailureServiceTimeMs,
    options.FailureErrorRate, options.FailureCapacityPerSecond,
    correlated.FreezeStartMs, correlated.RestartMs, correlated.OutageEndMs, correlated.ColdEndMs, correlated.ColdServiceTimeMs);

app.MapGet("/health", () => Results.Ok(new { status = "ok", in_system = Volatile.Read(ref current).InSystem }));

// The dependency itself. 200 = served; 503 = rejected (workers and queue full), injected transient
// failure (E03-A), failed by the restart or refused during the outage (E03-B).
app.MapGet("/work", async (HttpContext context) =>
{
    var run = Volatile.Read(ref current);
    var outcome = await WorkProcessor.ProcessAsync(run, options, context.RequestAborted);
    return outcome switch
    {
        WorkOutcome.Ok => Results.Ok(new { status = "ok" }),
        WorkOutcome.Rejected503 => Unavailable("overloaded: all workers busy and queue full"),
        WorkOutcome.Injected503 => Unavailable("injected transient failure"),
        WorkOutcome.Restart503 => Unavailable("dependency restarted while the request was inside"),
        WorkOutcome.Refused503 => Unavailable("dependency restarting"),
        _ => throw new InvalidOperationException($"unknown outcome {outcome}"),
    };
});

// Starts a fresh run: new counters, clock at zero, the failure armed relative to now.
// scenario=a (default): the E03-A window. scenario=b: the E03-B freeze and restart. failure=false: none.
// Refuses (409) while requests are still inside, so runs never overlap.
app.MapPost("/admin/reset", (string? runId, bool? failure, string? scenario) =>
{
    var previous = Volatile.Read(ref current);
    if (previous.InSystem > 0)
        return Results.Json(new { error = "dependency not idle", in_system = previous.InSystem }, statusCode: StatusCodes.Status409Conflict);

    var mode = (failure ?? true) == false ? FailureMode.None
        : scenario?.Trim().ToLowerInvariant() switch
        {
            null or "" or "a" => FailureMode.Window,
            "b" => FailureMode.CorrelatedRestart,
            var other => throw new BadHttpRequestException($"unknown scenario '{other}' (expected a or b)"),
        };

    var run = new ServerRun(runId ?? "unnamed", mode, options);
    Volatile.Write(ref current, run);
    using (log.BeginScope(new Dictionary<string, object?> { ["run_id"] = run.RunId }))
        log.LogInformation("phase={phase} failure_enabled={failure_enabled} mode={mode} Counters reset, clock at 0 s",
            "run_reset", run.FailureEnabled, run.Mode.ToString());

    if (mode == FailureMode.Window)
    {
        _ = LogWindowEdgeAsync(run, options.FailureStartSeconds, "failure_window_started",
            $"Degraded: service time {options.FailureServiceTimeMs} ms, {options.FailureErrorRate * 100:0}% injected 503");
        _ = LogWindowEdgeAsync(run, options.FailureEndSeconds, "failure_window_ended",
            $"Recovered: service time {options.ServiceTimeMs} ms, no injected errors");
    }
    else if (mode == FailureMode.CorrelatedRestart)
    {
        if (correlated.FreezeMs > 0)
            _ = LogEdgeAsync(run, correlated.FreezeStartMs, "freeze_started", "Frozen: nothing completes, every request is held until the restart");
        _ = RestartAtAsync(run);
        if (correlated.OutageMs > 0)
            _ = LogEdgeAsync(run, correlated.OutageEndMs, "outage_ended", "Restarted process accepts requests");
        if (correlated.ColdMs > 0)
            _ = LogEdgeAsync(run, correlated.ColdEndMs, "cold_ended", $"Warm: service time {options.ServiceTimeMs} ms");
    }
    return Results.Ok(run.Snapshot(options));
});

app.MapGet("/admin/stats", () => Results.Ok(Volatile.Read(ref current).Snapshot(options)));

app.MapPost("/admin/shutdown", (IHostApplicationLifetime lifetime) =>
{
    log.LogInformation("phase={phase} Shutdown requested by the load generator", "shutdown");
    lifetime.StopApplication();
    return Results.Ok(new { status = "stopping" });
});

app.Run();

static IResult Unavailable(string error) => Results.Json(new { error }, statusCode: StatusCodes.Status503ServiceUnavailable);

async Task WaitUntilAsync(ServerRun run, int atMs)
{
    var wait = TimeSpan.FromMilliseconds(atMs - run.ElapsedMs);
    if (wait > TimeSpan.Zero) await Task.Delay(wait);
}

// E03-A window edges (same log line as before E03-B was added).
async Task LogWindowEdgeAsync(ServerRun run, int atSecond, string phase, string message)
{
    await WaitUntilAsync(run, atSecond * 1000);
    if (!ReferenceEquals(Volatile.Read(ref current), run)) return; // a newer reset replaced this run
    using (log.BeginScope(new Dictionary<string, object?> { ["run_id"] = run.RunId }))
        log.LogInformation("phase={phase} t_s={t_s} in_system={in_system} {detail}", phase, atSecond, run.InSystem, message);
}

async Task LogEdgeAsync(ServerRun run, int atMs, string phase, string message)
{
    await WaitUntilAsync(run, atMs);
    if (!ReferenceEquals(Volatile.Read(ref current), run)) return; // a newer reset replaced this run
    using (log.BeginScope(new Dictionary<string, object?> { ["run_id"] = run.RunId }))
        log.LogInformation("phase={phase} t_ms={t_ms} in_system={in_system} {detail}", phase, atMs, run.InSystem, message);
}

async Task RestartAtAsync(ServerRun run)
{
    await WaitUntilAsync(run, correlated.RestartMs);
    if (!ReferenceEquals(Volatile.Read(ref current), run)) return;
    var held = run.Model.InSystem;
    run.Restart();
    using (log.BeginScope(new Dictionary<string, object?> { ["run_id"] = run.RunId }))
        log.LogInformation("phase={phase} t_ms={t_ms} actual_ms={actual_ms} held={held} {detail}", "restarted", correlated.RestartMs,
            Math.Round(run.RestartedAtMs, 1), held, $"Every request inside failed with 503; cold start for {correlated.ColdMs} ms");
}

public partial class Program;
