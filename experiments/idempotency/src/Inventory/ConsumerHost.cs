using Lab.Shared;
using Npgsql;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Inventory;

/// <summary>The consumer's crash point. Armed with POST /faults/crash-after-commit-before-ack.</summary>
public sealed class ConsumerFaults
{
    public const string CrashAfterCommitBeforeAck = "crash-after-commit-before-ack";

    /// <summary>SIGKILL after the consumer's transaction has ended (committed), before basic.ack is sent.</summary>
    public ArmedFault AfterCommitBeforeAck { get; } = new(CrashAfterCommitBeforeAck);
}

/// <summary>Shared between the consumer loop and /health.</summary>
public sealed class ConsumerState
{
    /// <summary>Random id per process start, stored with every delivery.</summary>
    public string Instance { get; } = Guid.NewGuid().ToString("N")[..8];

    public volatile bool Consuming;
}

/// <summary>Lab instrumentation: records every delivery (autocommit, outside the business transaction).</summary>
public sealed class DeliveryLog(NpgsqlDataSource db, InventoryOptions options, ConsumerState state)
{
    public async Task RecordAsync(Guid? eventId, string? messageId, string? operationId, ulong deliveryTag, bool redelivered, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            """
            INSERT INTO consumer_deliveries (consumer, instance, event_id, message_id, operation_id, delivery_tag, redelivered)
            VALUES (@consumer, @instance, @event_id, @message_id, @operation_id, @delivery_tag, @redelivered)
            """);
        cmd.Parameters.AddWithValue("consumer", options.ConsumerName);
        cmd.Parameters.AddWithValue("instance", state.Instance);
        cmd.Parameters.AddWithValue("event_id", (object?)eventId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("message_id", (object?)messageId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("operation_id", (object?)operationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("delivery_tag", (long)deliveryTag);
        cmd.Parameters.AddWithValue("redelivered", redelivered);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>
/// Consumes the consumer's own durable queue with manual acks and prefetch 1:
///   receive -> record delivery -> ReservationHandler (one DB transaction, COMMIT) -> basic.ack.
/// The ack is sent only after the transaction has committed. If the process dies between COMMIT
/// and ack, RabbitMQ requeues the unacked message when the connection closes and delivers it again
/// with redelivered=true. If processing throws, the message is nacked with requeue after 1 s.
/// (A message that always fails is redelivered forever: poison messages are out of scope.)
/// </summary>
public sealed class ConsumerHost(
    InventoryOptions options,
    ReservationHandler handler,
    DeliveryLog deliveryLog,
    ConsumerFaults faults,
    ConsumerState state,
    IProcessTerminator terminator,
    ILogger<ConsumerHost> logger) : BackgroundService
{
    public const string SealKit = "SK-101";

    // Held while a delivery is processed, so a graceful stop lets it finish (commit AND ack) first.
    private readonly SemaphoreSlim _inFlight = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await handler.EnsureStockAsync(SealKit, stoppingToken);

        var attempt = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            IConnection? connection = null;
            IChannel? channel = null;
            string? consumerTag = null;
            try
            {
                var factory = new ConnectionFactory
                {
                    Uri = new Uri(options.RabbitMqUri),
                    AutomaticRecoveryEnabled = false,
                    TopologyRecoveryEnabled = false,
                    RequestedConnectionTimeout = TimeSpan.FromSeconds(3),
                    ClientProvidedName = $"e02-inventory-{options.ConsumerName}",
                };
                connection = await factory.CreateConnectionAsync(stoppingToken);
                channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);

                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                connection.ConnectionShutdownAsync += (_, _) => { closed.TrySetResult(); return Task.CompletedTask; };
                channel.ChannelShutdownAsync += (_, _) => { closed.TrySetResult(); return Task.CompletedTask; };

                await DeclareTopologyAsync(channel, options, stoppingToken);
                await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false, stoppingToken);

                var consumer = new AsyncEventingBasicConsumer(channel);
                var ch = channel;
                consumer.ReceivedAsync += (_, delivery) => OnDeliveryAsync(ch, delivery);
                consumerTag = await channel.BasicConsumeAsync(options.Queue, autoAck: false, consumer, stoppingToken);

                state.Consuming = true;
                attempt = 0;
                logger.LogInformation("phase={phase} queue={queue} instance={instance} Consuming (manual ack, prefetch 1)",
                    "consumer_started", options.Queue, state.Instance);

                await closed.Task.WaitAsync(stoppingToken);
                state.Consuming = false;
                logger.LogWarning("phase={phase} Broker connection closed; reconnecting", "broker_disconnected");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                state.Consuming = false;
                attempt++;
                var delay = TimeSpan.FromMilliseconds(Math.Min(1000 * Math.Pow(2, attempt - 1), 10_000));
                logger.LogWarning(ex, "phase={phase} attempt={attempt} retry_in_ms={retry_in_ms} Cannot consume yet",
                    "broker_unavailable", attempt, (int)delay.TotalMilliseconds);
                try { await Task.Delay(delay, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
            finally
            {
                state.Consuming = false;
                await CloseAsync(channel, connection, consumerTag);
            }
        }
    }

    /// <summary>Exchange + this consumer's durable queue + binding. Idempotent.</summary>
    public static async Task DeclareTopologyAsync(IChannel channel, InventoryOptions options, CancellationToken ct)
    {
        await channel.ExchangeDeclareAsync(options.Exchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: ct);
        await channel.QueueDeclareAsync(options.Queue, durable: true, exclusive: false, autoDelete: false, cancellationToken: ct);
        await channel.QueueBindAsync(options.Queue, options.Exchange, options.RoutingKey, cancellationToken: ct);
    }

    private async Task OnDeliveryAsync(IChannel channel, BasicDeliverEventArgs delivery)
    {
        await _inFlight.WaitAsync();
        var props = delivery.BasicProperties;
        var evt = WorkOrderCreated.TryParse(delivery.Body.Span);
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["consumer"] = options.ConsumerName,
            ["event_id"] = evt?.EventId,
            ["message_id"] = props.MessageId,
            ["operation_id"] = evt?.OperationId ?? props.CorrelationId,
            ["delivery_tag"] = delivery.DeliveryTag,
            ["redelivered"] = delivery.Redelivered,
        });
        try
        {
            // Processing uses no cancellation token on purpose: a stop request must not abort a
            // transaction midway; the graceful stop waits for _inFlight instead.
            await deliveryLog.RecordAsync(evt?.EventId, props.MessageId, evt?.OperationId ?? props.CorrelationId,
                delivery.DeliveryTag, delivery.Redelivered, CancellationToken.None);
            logger.LogInformation("phase={phase} Delivery received", "delivery_received");

            if (evt is null)
            {
                logger.LogError("phase={phase} Unreadable WorkOrderCreated payload; rejecting without requeue", "message_rejected");
                await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false);
                return;
            }

            var result = await handler.HandleAsync(evt, CancellationToken.None);
            if (result.Outcome == ProcessingOutcome.Applied)
                logger.LogInformation("phase={phase} reservation_id={reservation_id} part_no={part_no} on_hand_after={on_hand_after} Reservation committed",
                    "effect_committed", result.ReservationId, evt.PartNo, result.OnHandAfter);
            else
                logger.LogInformation("phase={phase} Already processed by this consumer (processed_messages PK); nothing written",
                    "duplicate_skipped");

            // E02 crash point: the database work is committed, the broker has not seen an ack.
            if (faults.AfterCommitBeforeAck.TryFire(evt.OperationId))
            {
                logger.LogWarning("phase={phase} fault={fault} Transaction committed; terminating BEFORE basic.ack",
                    "fault_injected", ConsumerFaults.CrashAfterCommitBeforeAck);
                terminator.TerminateNow(ConsumerFaults.CrashAfterCommitBeforeAck);
            }

            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false);
            logger.LogInformation("phase={phase} Acked", "acked");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "phase={phase} Processing failed; nack with requeue in 1 s", "processing_failed");
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
                await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true);
            }
            catch (Exception nackError)
            {
                // Channel already gone: the broker requeues the unacked message by itself.
                logger.LogWarning(nackError, "phase={phase} Nack failed", "nack_failed");
            }
        }
        finally
        {
            _inFlight.Release();
        }
    }

    private async Task CloseAsync(IChannel? channel, IConnection? connection, string? consumerTag)
    {
        // Graceful stop: stop new deliveries, then let a delivery in progress finish (commit + ack)
        // before the channel goes away. A crash (SIGKILL) skips all of this, which is the point.
        if (channel is { IsOpen: true } && consumerTag is not null)
        {
            try { await channel.BasicCancelAsync(consumerTag); } catch { /* best effort */ }
        }
        if (await _inFlight.WaitAsync(TimeSpan.FromSeconds(10))) _inFlight.Release();
        try { if (channel is not null) await channel.DisposeAsync(); } catch { /* best effort */ }
        try { if (connection is not null) await connection.DisposeAsync(); } catch { /* best effort */ }
    }
}
