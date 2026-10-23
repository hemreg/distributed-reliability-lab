using Lab.Shared;
using Npgsql;
using WorkOrders;

var builder = WebApplication.CreateBuilder(args);
var options = WorkOrdersOptions.FromConfiguration(builder.Configuration);

builder.Logging.AddLabJsonConsole();

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(options.PostgresConnectionString));
builder.Services.AddSingleton<Broker>();
builder.Services.AddSingleton<IProcessTerminator, ProcessTerminator>();
builder.Services.AddSingleton<PublisherFaults>();
builder.Services.AddSingleton<WorkOrderService>();
builder.Services.AddSingleton<OutboxPublisher>();
if (options.Role == Role.OutboxWorker)
    builder.Services.AddHostedService<OutboxPublisherWorker>();

var app = builder.Build();
var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("WorkOrders");

await SchemaInitializer.ApplyAsync(app.Services.GetRequiredService<NpgsqlDataSource>(), log,
    typeof(WorkOrderService).Assembly, "WorkOrders.schema.sql");

log.LogInformation("phase={phase} role={role} fault_injection_enabled={fault_injection_enabled} Starting WorkOrders",
    "startup", options.RoleName, options.FaultInjectionEnabled);

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    role = options.RoleName,
    fault_injection_enabled = options.FaultInjectionEnabled,
}));

if (options.Role == Role.Api)
{
    app.MapPost("/work-orders", async (HttpRequest request, WorkOrderService service) =>
    {
        var body = request.ContentLength > 0 ? await request.ReadFromJsonAsync<CreateWorkOrderRequest>() : null;
        var operationId = request.Headers["X-Operation-Id"].FirstOrDefault() is { Length: > 0 } op ? op : $"op-{Guid.CreateVersion7()}";

        // No request-abort token on purpose: a client disconnect must not cancel the work midway.
        var result = await service.CreateAsync(body?.Title ?? "Replace seal on pump P-101", operationId);
        return Results.Created($"/work-orders/{result.WorkOrderId}", new
        {
            work_order_id = result.WorkOrderId,
            event_id = result.EventId,
            operation_id = result.OperationId,
            part_no = WorkOrderCreated.SealKit,
            quantity = 1,
            event_status = result.EventStatus,
        });
    });
}

if (options.Role == Role.OutboxWorker)
{
    // LOCAL LAB ONLY. Arms a one-shot crash: after the broker confirms the event of operation_id
    // (or the next event, without it), SIGKILL before the outbox row is marked published.
    app.MapPost($"/faults/{PublisherFaults.CrashAfterPublishConfirm}", (string? operation_id, PublisherFaults faults) =>
    {
        if (!options.FaultInjectionEnabled)
            return Results.BadRequest(new { error = "Fault injection requires E02_FAULT_INJECTION_ENABLED=true (local lab only)." });
        faults.AfterConfirmBeforeMark.Arm(operation_id);
        log.LogWarning("phase={phase} fault={fault} operation_id={operation_id} Fault armed (one shot, in memory)",
            "fault_armed", PublisherFaults.CrashAfterPublishConfirm, operation_id ?? "(next event)");
        return Results.Accepted(value: new { armed = PublisherFaults.CrashAfterPublishConfirm, operation_id });
    });
}

app.Run();

public sealed record CreateWorkOrderRequest(string? Title);
