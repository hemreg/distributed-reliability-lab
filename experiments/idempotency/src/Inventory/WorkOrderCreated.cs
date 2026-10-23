using System.Text.Json;

namespace Inventory;

/// <summary>
/// The consumer's own copy of the event contract (it does not reference the producer's code).
/// EventId is the outbox event_id: the same on every publish attempt of the same event.
/// </summary>
public sealed record WorkOrderCreated(
    Guid EventId, Guid WorkOrderId, string Title, string OperationId, string PartNo, int Quantity, DateTimeOffset OccurredAt)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static WorkOrderCreated? TryParse(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            var evt = JsonSerializer.Deserialize<WorkOrderCreated>(utf8Json, Json);
            return evt is { EventId: var id, Quantity: > 0, PartNo.Length: > 0, OperationId: not null } && id != Guid.Empty ? evt : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);
}
