using Lab.Shared;

namespace WorkOrders;

public enum Role
{
    /// <summary>HTTP API: inserts the work order and its outbox row in one transaction. No broker I/O.</summary>
    Api,

    /// <summary>Outbox worker: relays pending outbox rows to RabbitMQ with publisher confirms, then marks them.</summary>
    OutboxWorker,
}

/// <summary>
/// All settings come from environment variables with explicit E02_* names, so every toggle the
/// experiment uses is visible in compose.yaml.
/// </summary>
public sealed record WorkOrdersOptions
{
    public required Role Role { get; init; }
    public required string PostgresConnectionString { get; init; }
    public required string RabbitMqUri { get; init; }

    /// <summary>Local-lab only. When false, the /faults endpoints answer 400.</summary>
    public bool FaultInjectionEnabled { get; init; }

    public string Exchange { get; init; } = "e02.work-orders";
    public string RoutingKey { get; init; } = "work-order.created";

    /// <summary>
    /// Durable queue bound to the exchange that nothing consumes. Every message the broker accepted
    /// stays in it, so the experiment can count how many copies of one event_id were published.
    /// </summary>
    public string AuditQueue { get; init; } = "e02.work-order-created.audit";

    public TimeSpan PublisherPollInterval { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan PublisherRetryBaseDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan PublisherRetryMaxDelay { get; init; } = TimeSpan.FromSeconds(10);
    public int PublisherBatchSize { get; init; } = 10;
    public TimeSpan PublishConfirmTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public static WorkOrdersOptions FromConfiguration(IConfiguration config)
    {
        var role = config["E02_ROLE"]?.Trim().ToLowerInvariant() switch
        {
            "api" => Role.Api,
            "outbox-worker" => Role.OutboxWorker,
            var other => throw new InvalidOperationException(
                $"E02_ROLE must be 'api' or 'outbox-worker' (got '{other ?? "<unset>"}')."),
        };

        var defaults = new WorkOrdersOptions { Role = role, PostgresConnectionString = "", RabbitMqUri = "" };
        return defaults with
        {
            PostgresConnectionString = Required(config, "E02_POSTGRES"),
            RabbitMqUri = Required(config, "E02_RABBITMQ_URI"),
            FaultInjectionEnabled = LabLogging.Bool(config["E02_FAULT_INJECTION_ENABLED"], false),
            Exchange = config["E02_EXCHANGE"] ?? defaults.Exchange,
            RoutingKey = config["E02_ROUTING_KEY"] ?? defaults.RoutingKey,
            AuditQueue = config["E02_AUDIT_QUEUE"] ?? defaults.AuditQueue,
            PublisherPollInterval = config["E02_PUBLISHER_POLL_MS"] is { Length: > 0 } ms
                ? TimeSpan.FromMilliseconds(int.Parse(ms)) : defaults.PublisherPollInterval,
        };
    }

    public string RoleName => Role == Role.Api ? "api" : "outbox-worker";

    private static string Required(IConfiguration config, string key) =>
        string.IsNullOrWhiteSpace(config[key]) ? throw new InvalidOperationException($"{key} is required.") : config[key]!;
}
