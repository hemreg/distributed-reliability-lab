using System.Text.Json;

namespace WorkOrders;

/// <summary>
/// The integration event. EventId is generated once, stored in the outbox row and reused on
/// every publish attempt, so a re-published copy carries the same EventId.
/// Every work order needs one seal kit, which the inventory consumers reserve.
/// </summary>
public sealed record WorkOrderCreated(
    Guid EventId, Guid WorkOrderId, string Title, string OperationId, string PartNo, int Quantity, DateTimeOffset OccurredAt)
{
    public const string EventType = "WorkOrderCreated";
    public const string SealKit = "SK-101";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public string ToJson() => JsonSerializer.Serialize(this, Json);
}

/// <summary>What the broker adapter needs to publish one outbox row.</summary>
public sealed record OutgoingEvent(Guid EventId, string EventType, Guid AggregateId, string OperationId, string PayloadJson);
