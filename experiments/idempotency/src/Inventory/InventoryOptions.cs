using Lab.Shared;

namespace Inventory;

public enum ConsumerMode
{
    /// <summary>UNSAFE BASELINE: every delivery reserves the part again. A duplicate reduces stock twice.</summary>
    Naive,

    /// <summary>MITIGATION: processed_messages insert + reservation + stock update in one transaction; the PK decides duplicates.</summary>
    Idempotent,
}

public sealed record InventoryOptions
{
    public required ConsumerMode Mode { get; init; }

    /// <summary>Scope of deduplication and of the stock level: the `consumer` column. Defaults to the mode.</summary>
    public required string ConsumerName { get; init; }

    public required string PostgresConnectionString { get; init; }
    public required string RabbitMqUri { get; init; }

    /// <summary>Local-lab only. When false, the /faults endpoint answers 400.</summary>
    public bool FaultInjectionEnabled { get; init; }

    public string Exchange { get; init; } = "e02.work-orders";
    public string RoutingKey { get; init; } = "work-order.created";
    public required string Queue { get; init; }

    /// <summary>Stock seeded once per consumer for the seal kit every work order needs.</summary>
    public int InitialStock { get; init; } = 10;

    public string ModeName => Mode == ConsumerMode.Naive ? "naive" : "idempotent";

    public static InventoryOptions FromConfiguration(IConfiguration config)
    {
        var mode = config["E02_CONSUMER_MODE"]?.Trim().ToLowerInvariant() switch
        {
            "naive" => ConsumerMode.Naive,
            "idempotent" => ConsumerMode.Idempotent,
            var other => throw new InvalidOperationException(
                $"E02_CONSUMER_MODE must be 'naive' or 'idempotent' (got '{other ?? "<unset>"}'). The consumer mode is never implicit."),
        };
        var name = config["E02_CONSUMER_NAME"] is { Length: > 0 } n ? n : mode == ConsumerMode.Naive ? "naive" : "idempotent";

        return new InventoryOptions
        {
            Mode = mode,
            ConsumerName = name,
            PostgresConnectionString = Required(config, "E02_POSTGRES"),
            RabbitMqUri = Required(config, "E02_RABBITMQ_URI"),
            FaultInjectionEnabled = LabLogging.Bool(config["E02_FAULT_INJECTION_ENABLED"], false),
            Exchange = config["E02_EXCHANGE"] ?? "e02.work-orders",
            RoutingKey = config["E02_ROUTING_KEY"] ?? "work-order.created",
            Queue = config["E02_QUEUE"] is { Length: > 0 } q ? q : $"e02.inventory.{name}",
            InitialStock = config["E02_INITIAL_STOCK"] is { Length: > 0 } s ? int.Parse(s) : 10,
        };
    }

    private static string Required(IConfiguration config, string key) =>
        string.IsNullOrWhiteSpace(config[key]) ? throw new InvalidOperationException($"{key} is required.") : config[key]!;
}
