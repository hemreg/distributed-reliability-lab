using Inventory;
using Lab.Shared;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var options = InventoryOptions.FromConfiguration(builder.Configuration);

builder.Logging.AddLabJsonConsole();

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(options.PostgresConnectionString));
builder.Services.AddSingleton<IProcessTerminator, ProcessTerminator>();
builder.Services.AddSingleton<ConsumerState>();
builder.Services.AddSingleton<ConsumerFaults>();
builder.Services.AddSingleton<DeliveryLog>();
builder.Services.AddSingleton(sp => new ReservationHandler(sp.GetRequiredService<NpgsqlDataSource>(), options));
builder.Services.AddHostedService<ConsumerHost>();

var app = builder.Build();
var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Inventory");

await SchemaInitializer.ApplyAsync(app.Services.GetRequiredService<NpgsqlDataSource>(), log,
    typeof(ReservationHandler).Assembly, "Inventory.schema.sql");

log.LogInformation("phase={phase} consumer={consumer} mode={mode} queue={queue} fault_injection_enabled={fault_injection_enabled} Starting inventory consumer",
    "startup", options.ConsumerName, options.ModeName, options.Queue, options.FaultInjectionEnabled);
if (options.Mode == ConsumerMode.Naive)
    log.LogWarning("phase={phase} consumer={consumer} mode={mode} UNSAFE BASELINE: every delivery reserves the part again; duplicates reduce stock twice",
        "startup", options.ConsumerName, options.ModeName);

// 200 only while the consumer is attached to its queue.
app.MapGet("/health", (ConsumerState state) => Results.Json(new
{
    status = state.Consuming ? "ok" : "not_consuming",
    consumer = options.ConsumerName,
    mode = options.ModeName,
    queue = options.Queue,
    instance = state.Instance,
    fault_injection_enabled = options.FaultInjectionEnabled,
}, statusCode: state.Consuming ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable));

// LOCAL LAB ONLY. Arms a one-shot crash: after the transaction for the message of operation_id
// (or the next message, without it) has committed, SIGKILL before basic.ack.
app.MapPost($"/faults/{ConsumerFaults.CrashAfterCommitBeforeAck}", (string? operation_id, ConsumerFaults faults) =>
{
    if (!options.FaultInjectionEnabled)
        return Results.BadRequest(new { error = "Fault injection requires E02_FAULT_INJECTION_ENABLED=true (local lab only)." });
    faults.AfterCommitBeforeAck.Arm(operation_id);
    log.LogWarning("phase={phase} fault={fault} operation_id={operation_id} Fault armed (one shot, in memory)",
        "fault_armed", ConsumerFaults.CrashAfterCommitBeforeAck, operation_id ?? "(next message)");
    return Results.Accepted(value: new { armed = ConsumerFaults.CrashAfterCommitBeforeAck, consumer = options.ConsumerName, operation_id });
});

app.Run();
