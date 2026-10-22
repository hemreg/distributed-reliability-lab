using Microsoft.Extensions.Logging.Console;
using Npgsql;
using WorkOrders;

var builder = WebApplication.CreateBuilder(args);
var options = E01Options.FromConfiguration(builder.Configuration);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.FormatterName = CompactJsonConsoleFormatter.FormatterName);
builder.Logging.AddConsoleFormatter<CompactJsonConsoleFormatter, ConsoleFormatterOptions>();
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(options.PostgresConnectionString));
builder.Services.AddSingleton<Broker>();
builder.Services.AddSingleton<IProcessTerminator, ProcessTerminator>();
builder.Services.AddSingleton<WorkOrderService>();
builder.Services.AddSingleton<OutboxPublisher>();
if (options is { Mode: WriteMode.Outbox, OutboxPublisherEnabled: true })
    builder.Services.AddHostedService<OutboxPublisherWorker>();

var app = builder.Build();
var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("WorkOrders");

await SchemaInitializer.ApplyAsync(app.Services.GetRequiredService<NpgsqlDataSource>(), log);

var modeName = options.Mode.ToString().ToLowerInvariant();
log.LogInformation("phase={phase} mode={mode} outbox_publisher_enabled={outbox_publisher_enabled} fault_injection_enabled={fault_injection_enabled} Starting WorkOrders",
    "startup", modeName, options is { Mode: WriteMode.Outbox, OutboxPublisherEnabled: true }, options.FaultInjectionEnabled);
if (options.Mode == WriteMode.Naive)
    log.LogWarning("phase={phase} mode={mode} UNSAFE BASELINE: commit and publish are separate steps (dual write). Events can be lost.",
        "startup", modeName);
if (options is { Mode: WriteMode.Outbox, OutboxPublisherEnabled: false })
    log.LogWarning("phase={phase} Outbox publisher is DISABLED: rows will stay pending until a publisher runs", "publisher_disabled");

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    mode = modeName,
    outbox_publisher_enabled = options is { Mode: WriteMode.Outbox, OutboxPublisherEnabled: true },
    fault_injection_enabled = options.FaultInjectionEnabled,
}));

app.MapPost("/work-orders", async (HttpRequest request, WorkOrderService service) =>
{
    var body = request.ContentLength > 0 ? await request.ReadFromJsonAsync<CreateWorkOrderRequest>() : null;
    var operationId = request.Headers["X-Operation-Id"].FirstOrDefault() is { Length: > 0 } op ? op : $"op-{Guid.CreateVersion7()}";

    var faultHeader = request.Headers[FaultHeader.Name].FirstOrDefault();
    if (!FaultHeader.TryParse(faultHeader, out var fault))
        return Results.BadRequest(new { error = $"Unknown {FaultHeader.Name} value '{faultHeader}'." });
    if (fault != InjectedFault.None && !options.FaultInjectionEnabled)
        return Results.BadRequest(new { error = $"{FaultHeader.Name} requires E01_FAULT_INJECTION_ENABLED=true (local lab only)." });

    try
    {
        // No request-abort token on purpose: a client disconnect must not cancel the work midway.
        var result = await service.CreateAsync(options.Mode, body?.Title ?? "Inspect pump P-101", operationId, fault);
        return Results.Created($"/work-orders/{result.WorkOrderId}", new
        {
            mode = modeName,
            work_order_id = result.WorkOrderId,
            event_id = result.EventId,
            operation_id = result.OperationId,
            event_status = result.EventStatus,
        });
    }
    catch (InjectedFaultException ex)
    {
        return Results.Json(new
        {
            error = ex.Message,
            mode = modeName,
            operation_id = operationId,
            attempted_work_order_id = ex.AttemptedWorkOrderId,
            attempted_event_id = ex.AttemptedEventId,
        }, statusCode: StatusCodes.Status500InternalServerError);
    }
    catch (EventNotPublishedException ex)
    {
        // Honest response for the baseline: the state change happened, the event did not.
        return Results.Json(new
        {
            error = ex.Message,
            mode = modeName,
            operation_id = operationId,
            work_order_id = ex.WorkOrderId,
            event_id = ex.EventId,
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.Run();

public sealed record CreateWorkOrderRequest(string? Title);

public partial class Program;
