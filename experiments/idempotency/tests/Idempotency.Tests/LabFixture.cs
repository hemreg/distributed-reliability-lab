using System.Text;
using Inventory;
using Lab.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using RabbitMQ.Client;
using WorkOrders;

[assembly: AssemblyFixture(typeof(Idempotency.Tests.LabFixture))]
// Tests share one database and one set of queues and reset them before each test.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Idempotency.Tests;

/// <summary>
/// Connects to the real PostgreSQL and RabbitMQ from the compose stack. Uses a dedicated database
/// (e02_tests) and dedicated exchange/queue names, so tests never touch the state of run-e02.sh.
/// </summary>
public sealed class LabFixture : IAsyncLifetime
{
    private const string TestDatabase = "e02_tests";
    public const int InitialStock = 10;

    public const string Exchange = "e02.tests.work-orders";
    public const string AuditQueue = "e02.tests.work-order-created.audit";
    public const string Naive = "naive";
    public const string Idempotent = "idempotent";

    public string PostgresConnectionString { get; private set; } = "";
    public string RabbitMqUri { get; } = Env("E02_TEST_RABBITMQ_URI", "amqp://lab:lab@localhost:5672/");
    public NpgsqlDataSource Db { get; private set; } = null!;

    public static string QueueOf(string consumer) => $"e02.tests.inventory.{consumer}";

    public WorkOrdersOptions WorkOrdersOptions => new()
    {
        Role = Role.OutboxWorker,
        PostgresConnectionString = PostgresConnectionString,
        RabbitMqUri = RabbitMqUri,
        FaultInjectionEnabled = true,
        Exchange = Exchange,
        AuditQueue = AuditQueue,
        PublisherPollInterval = TimeSpan.FromMilliseconds(200),
    };

    public InventoryOptions ConsumerOptions(ConsumerMode mode, string? name = null)
    {
        var consumer = name ?? (mode == ConsumerMode.Naive ? Naive : Idempotent);
        return new InventoryOptions
        {
            Mode = mode,
            ConsumerName = consumer,
            PostgresConnectionString = PostgresConnectionString,
            RabbitMqUri = RabbitMqUri,
            FaultInjectionEnabled = true,
            Exchange = Exchange,
            Queue = QueueOf(consumer),
            InitialStock = InitialStock,
        };
    }

    /// <summary>Environment for the real outbox worker process.</summary>
    public Dictionary<string, string> WorkerEnvironment() => new()
    {
        ["E02_ROLE"] = "outbox-worker",
        ["E02_POSTGRES"] = PostgresConnectionString,
        ["E02_RABBITMQ_URI"] = RabbitMqUri,
        ["E02_FAULT_INJECTION_ENABLED"] = "true",
        ["E02_EXCHANGE"] = Exchange,
        ["E02_AUDIT_QUEUE"] = AuditQueue,
        ["E02_PUBLISHER_POLL_MS"] = "200",
    };

    /// <summary>Environment for a real inventory consumer process.</summary>
    public Dictionary<string, string> ConsumerEnvironment(ConsumerMode mode)
    {
        var o = ConsumerOptions(mode);
        return new()
        {
            ["E02_CONSUMER_MODE"] = o.ModeName,
            ["E02_CONSUMER_NAME"] = o.ConsumerName,
            ["E02_POSTGRES"] = PostgresConnectionString,
            ["E02_RABBITMQ_URI"] = RabbitMqUri,
            ["E02_FAULT_INJECTION_ENABLED"] = "true",
            ["E02_EXCHANGE"] = Exchange,
            ["E02_QUEUE"] = o.Queue,
            ["E02_INITIAL_STOCK"] = InitialStock.ToString(),
        };
    }

    public async ValueTask InitializeAsync()
    {
        var admin = new NpgsqlConnectionStringBuilder(Env("E02_TEST_POSTGRES_ADMIN", "Host=localhost;Username=lab;Password=lab;Database=postgres"));
        await using (var adminDb = NpgsqlDataSource.Create(admin.ConnectionString))
        await using (var conn = await adminDb.OpenConnectionAsync())
        {
            await using var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @name", conn);
            exists.Parameters.AddWithValue("name", TestDatabase);
            if (await exists.ExecuteScalarAsync() is null)
                await new NpgsqlCommand($"CREATE DATABASE {TestDatabase}", conn).ExecuteNonQueryAsync();
        }

        admin.Database = TestDatabase;
        PostgresConnectionString = admin.ConnectionString;
        Db = NpgsqlDataSource.Create(PostgresConnectionString);
        await SchemaInitializer.ApplyAsync(Db, NullLogger.Instance, typeof(WorkOrderService).Assembly, "WorkOrders.schema.sql");
        await SchemaInitializer.ApplyAsync(Db, NullLogger.Instance, typeof(ReservationHandler).Assembly, "Inventory.schema.sql");
    }

    public async ValueTask DisposeAsync() => await Db.DisposeAsync();

    /// <summary>Empties all tables and queues, declares the topology, seeds both consumers' stock.</summary>
    public async Task ResetAsync()
    {
        await Execute("TRUNCATE work_orders, outbox_messages, stock_levels, part_reservations, processed_messages, consumer_deliveries");
        await WithChannel(async ch =>
        {
            await Broker.DeclareTopologyAsync(ch, WorkOrdersOptions, default);
            foreach (var mode in new[] { ConsumerMode.Naive, ConsumerMode.Idempotent })
            {
                await ConsumerHost.DeclareTopologyAsync(ch, ConsumerOptions(mode), default);
                await ch.QueuePurgeAsync(ConsumerOptions(mode).Queue);
            }
            await ch.QueuePurgeAsync(AuditQueue);
        });
        foreach (var mode in new[] { ConsumerMode.Naive, ConsumerMode.Idempotent })
            await Handler(mode).EnsureStockAsync(ConsumerHost.SealKit);
    }

    public ReservationHandler Handler(ConsumerMode mode, string? name = null,
        Func<Inventory.WorkOrderCreated, CancellationToken, Task>? beforeCommit = null) =>
        new(Db, ConsumerOptions(mode, name), beforeCommit);

    public WorkOrderService WorkOrderService() => new(Db, NullLogger<WorkOrderService>.Instance);

    // ---- broker -------------------------------------------------------------------------------

    public Task<uint> QueueMessageCountAsync(string queue) => WithChannel(ch => ch.MessageCountAsync(queue));

    /// <summary>Consumes (acks) everything currently in the queue.</summary>
    public Task<List<ReceivedMessage>> DrainQueueAsync(string queue) => WithChannel(async ch =>
    {
        var messages = new List<ReceivedMessage>();
        while (await ch.BasicGetAsync(queue, autoAck: true) is { } msg)
            messages.Add(new ReceivedMessage(msg.BasicProperties.MessageId, msg.BasicProperties.CorrelationId,
                msg.Redelivered, Encoding.UTF8.GetString(msg.Body.Span)));
        return messages;
    });

    /// <summary>Publishes one copy of an outbox row's payload, exactly as the outbox worker would (same message_id).</summary>
    public async Task PublishCopyAsync(Guid eventId)
    {
        var payload = await Scalar<string>("SELECT payload::text FROM outbox_messages WHERE event_id = @id", ("id", eventId));
        var operationId = await Scalar<string>("SELECT operation_id FROM outbox_messages WHERE event_id = @id", ("id", eventId));
        await WithChannel(async ch =>
        {
            var props = new BasicProperties { MessageId = eventId.ToString(), CorrelationId = operationId, DeliveryMode = DeliveryModes.Persistent };
            await ch.BasicPublishAsync(Exchange, "work-order.created", mandatory: false, basicProperties: props, body: Encoding.UTF8.GetBytes(payload));
        });
    }

    // ---- database -----------------------------------------------------------------------------

    public async Task<int> Execute(string sql)
    {
        await using var cmd = Db.CreateCommand(sql);
        return await cmd.ExecuteNonQueryAsync();
    }

    public async Task<T> Scalar<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var cmd = Db.CreateCommand(sql);
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    public Task<long> ReservationsAsync(string consumer, Guid eventId) =>
        Scalar<long>("SELECT count(*) FROM part_reservations WHERE consumer = @c AND event_id = @e", ("c", consumer), ("e", eventId));

    public Task<long> ReservationsAsync(string consumer) =>
        Scalar<long>("SELECT count(*) FROM part_reservations WHERE consumer = @c", ("c", consumer));

    public Task<int> OnHandAsync(string consumer) =>
        Scalar<int>("SELECT on_hand FROM stock_levels WHERE consumer = @c AND part_no = @p", ("c", consumer), ("p", ConsumerHost.SealKit));

    public Task<long> ProcessedAsync(string consumer) =>
        Scalar<long>("SELECT count(*) FROM processed_messages WHERE consumer = @c", ("c", consumer));

    public async Task<List<bool>> DeliveriesAsync(string consumer, Guid eventId)
    {
        var flags = new List<bool>();
        await using var cmd = Db.CreateCommand("SELECT redelivered FROM consumer_deliveries WHERE consumer = @c AND event_id = @e ORDER BY id");
        cmd.Parameters.AddWithValue("c", consumer);
        cmd.Parameters.AddWithValue("e", eventId);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) flags.Add(reader.GetBoolean(0));
        return flags;
    }

    public Task<long> PendingOutboxRowsAsync() => Scalar<long>("SELECT count(*) FROM outbox_messages WHERE published_at IS NULL");

    /// <summary>Polls until the condition holds; fails the test after the timeout.</summary>
    public static async Task WaitUntilAsync(Func<Task<bool>> condition, string what, int timeoutSeconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"timed out after {timeoutSeconds}s waiting for: {what}");
            await Task.Delay(100);
        }
    }

    private async Task<T> WithChannel<T>(Func<IChannel, Task<T>> action)
    {
        await using var conn = await new ConnectionFactory { Uri = new Uri(RabbitMqUri) }.CreateConnectionAsync();
        await using var ch = await conn.CreateChannelAsync();
        return await action(ch);
    }

    private Task WithChannel(Func<IChannel, Task> action) => WithChannel(async ch => { await action(ch); return 0; });

    private static string Env(string key, string fallback) =>
        Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : fallback;
}

public sealed record ReceivedMessage(string? MessageId, string? CorrelationId, bool Redelivered, string Body);
