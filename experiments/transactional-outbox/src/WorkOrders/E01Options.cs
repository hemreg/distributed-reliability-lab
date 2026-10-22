namespace WorkOrders;

public enum WriteMode
{
    /// <summary>UNSAFE BASELINE: commit to PostgreSQL, then publish to RabbitMQ as a separate step (dual write).</summary>
    Naive,

    /// <summary>MITIGATION: work order + outbox row in one transaction; a background publisher relays pending rows.</summary>
    Outbox,
}

/// <summary>
/// All settings come from environment variables (or any other IConfiguration source)
/// with explicit E01_* names, so every toggle used by the experiment is visible in compose.yaml.
/// </summary>
public sealed record E01Options
{
    public required WriteMode Mode { get; init; }
    public required string PostgresConnectionString { get; init; }
    public required string RabbitMqUri { get; init; }

    /// <summary>Local-lab only. When false, any X-Fault-Inject header is rejected with 400.</summary>
    public bool FaultInjectionEnabled { get; init; }

    /// <summary>Outbox mode only. False simulates "the publisher is stopped": rows stay pending.</summary>
    public bool OutboxPublisherEnabled { get; init; } = true;

    public string Exchange { get; init; } = "e01.work-orders";
    public string RoutingKey { get; init; } = "work-order.created";

    /// <summary>
    /// Durable queue bound to the exchange. It stands in for a downstream consumer so the
    /// experiment can count what actually reached the broker. Nothing consumes it.
    /// </summary>
    public string EvidenceQueue { get; init; } = "e01.work-order-created";

    public TimeSpan PublisherPollInterval { get; init; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan PublisherRetryBaseDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan PublisherRetryMaxDelay { get; init; } = TimeSpan.FromSeconds(10);
    public int PublisherBatchSize { get; init; } = 10;
    public TimeSpan PublishConfirmTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public static E01Options FromConfiguration(IConfiguration config)
    {
        var mode = config["E01_MODE"]?.Trim().ToLowerInvariant() switch
        {
            "naive" => WriteMode.Naive,
            "outbox" => WriteMode.Outbox,
            var other => throw new InvalidOperationException(
                $"E01_MODE must be 'naive' or 'outbox' (got '{other ?? "<unset>"}'). The write mode is never implicit."),
        };

        var defaults = new E01Options { Mode = mode, PostgresConnectionString = "", RabbitMqUri = "" };
        return defaults with
        {
            PostgresConnectionString = Required(config, "E01_POSTGRES"),
            RabbitMqUri = Required(config, "E01_RABBITMQ_URI"),
            FaultInjectionEnabled = Bool(config, "E01_FAULT_INJECTION_ENABLED", false),
            OutboxPublisherEnabled = Bool(config, "E01_OUTBOX_PUBLISHER_ENABLED", true),
            Exchange = config["E01_EXCHANGE"] ?? defaults.Exchange,
            RoutingKey = config["E01_ROUTING_KEY"] ?? defaults.RoutingKey,
            EvidenceQueue = config["E01_EVIDENCE_QUEUE"] ?? defaults.EvidenceQueue,
        };
    }

    private static string Required(IConfiguration config, string key) =>
        string.IsNullOrWhiteSpace(config[key]) ? throw new InvalidOperationException($"{key} is required.") : config[key]!;

    private static bool Bool(IConfiguration config, string key, bool fallback) =>
        config[key] is { } raw ? bool.Parse(raw) : fallback;
}
