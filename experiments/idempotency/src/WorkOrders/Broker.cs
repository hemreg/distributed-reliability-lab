using System.Text;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace WorkOrders;

/// <summary>
/// Minimal RabbitMQ publisher with publisher confirms.
///
/// PublishAsync returns only after the broker has confirmed the message (basic.ack).
/// It throws if the broker is unreachable, nacks the message, returns it as unroutable
/// (mandatory=true), or does not confirm within the timeout. A thrown exception does NOT
/// prove the broker did not receive the message: the outcome can be uncertain.
///
/// A confirm means the broker has taken responsibility for the message (routed it to the bound
/// durable queues). It says nothing about whether any consumer received or processed it.
///
/// Connection handling is deliberately explicit (no automatic recovery): on any failure the
/// connection is dropped and re-created on the next call.
/// </summary>
public sealed class Broker(WorkOrdersOptions options, ILogger<Broker> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;
    private IChannel? _channel;

    public async Task PublishAsync(OutgoingEvent evt, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(options.PublishConfirmTimeout);

        await _gate.WaitAsync(ct);
        try
        {
            var channel = await EnsureChannelAsync(timeout.Token);
            var props = new BasicProperties
            {
                MessageId = evt.EventId.ToString(),
                Type = evt.EventType,
                CorrelationId = evt.OperationId,
                ContentType = "application/json",
                DeliveryMode = DeliveryModes.Persistent,
                Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            };
            // With confirmation tracking enabled this awaits the broker's confirm.
            await channel.BasicPublishAsync(options.Exchange, options.RoutingKey, mandatory: true,
                basicProperties: props, body: Encoding.UTF8.GetBytes(evt.PayloadJson), cancellationToken: timeout.Token);
        }
        catch
        {
            await ResetAsync();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IChannel> EnsureChannelAsync(CancellationToken ct)
    {
        if (_channel is { IsOpen: true }) return _channel;
        await ResetAsync();

        var factory = new ConnectionFactory
        {
            Uri = new Uri(options.RabbitMqUri),
            AutomaticRecoveryEnabled = false,
            TopologyRecoveryEnabled = false,
            RequestedConnectionTimeout = TimeSpan.FromSeconds(3),
            ClientProvidedName = $"e02-workorders-{options.RoleName}",
        };
        _connection = await factory.CreateConnectionAsync(ct);
        _channel = await _connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), ct);

        await DeclareTopologyAsync(_channel, options, ct);
        logger.LogInformation("phase={phase} exchange={exchange} queue={queue} Connected to RabbitMQ (publisher confirms on)",
            "broker_connected", options.Exchange, options.AuditQueue);
        return _channel;
    }

    /// <summary>
    /// Durable topic exchange plus the durable audit queue bound to it. Idempotent. The inventory
    /// consumers declare and bind their own queues.
    /// </summary>
    public static async Task DeclareTopologyAsync(IChannel channel, WorkOrdersOptions options, CancellationToken ct)
    {
        await channel.ExchangeDeclareAsync(options.Exchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: ct);
        await channel.QueueDeclareAsync(options.AuditQueue, durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
        await channel.QueueBindAsync(options.AuditQueue, options.Exchange, options.RoutingKey, cancellationToken: ct);
    }

    public static bool IsBrokerUnavailable(Exception ex) =>
        ex is BrokerUnreachableException or ConnectFailureException or AlreadyClosedException;

    private async Task ResetAsync()
    {
        try { if (_channel is not null) await _channel.DisposeAsync(); } catch { /* best effort */ }
        try { if (_connection is not null) await _connection.DisposeAsync(); } catch { /* best effort */ }
        _channel = null;
        _connection = null;
    }

    public async ValueTask DisposeAsync() => await ResetAsync();
}
