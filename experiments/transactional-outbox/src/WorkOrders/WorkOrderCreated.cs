using System.Text.Json;

namespace WorkOrders;

/// <summary>The integration event. EventId is generated once and never changes.</summary>
public sealed record WorkOrderCreated(Guid EventId, Guid WorkOrderId, string Title, string OperationId, DateTimeOffset OccurredAt)
{
    public const string EventType = "WorkOrderCreated";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public string ToJson() => JsonSerializer.Serialize(this, Json);
}

/// <summary>What the broker adapter needs to publish one event, from either write path.</summary>
public sealed record OutgoingEvent(Guid EventId, string EventType, Guid AggregateId, string OperationId, string PayloadJson);
