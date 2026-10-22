using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using RabbitMQ.Client;

[assembly: AssemblyFixture(typeof(WorkOrders.Tests.LabFixture))]
// Tests share one database and one queue and reset them before each test.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace WorkOrders.Tests;

/// <summary>
/// Connects to the real PostgreSQL and RabbitMQ from the compose stack. Uses a dedicated
/// database (e01_tests) and dedicated exchange/queue names, so tests never touch the state
/// produced by run-e01.sh.
/// </summary>
public sealed class LabFixture : IAsyncLifetime
{
    private const string TestDatabase = "e01_tests";

    public string PostgresConnectionString { get; private set; } = "";
    public string RabbitMqUri { get; } = Env("E01_TEST_RABBITMQ_URI", "amqp://lab:lab@localhost:5672/");

    /// <summary>Nothing listens on port 1: connection refused, i.e. "broker unavailable".</summary>
    public string UnreachableRabbitMqUri => new UriBuilder(RabbitMqUri) { Host = "127.0.0.1", Port = 1 }.Uri.ToString();

    public NpgsqlDataSource Db { get; private set; } = null!;

    public E01Options Options(WriteMode mode) => new()
    {
        Mode = mode,
        PostgresConnectionString = PostgresConnectionString,
        RabbitMqUri = RabbitMqUri,
        FaultInjectionEnabled = true,
        Exchange = "e01.tests.work-orders",
        EvidenceQueue = "e01.tests.work-order-created",
        PublisherPollInterval = TimeSpan.FromMilliseconds(200),
    };

    /// <summary>Environment for running the real WorkOrders process against the test resources.</summary>
    public Dictionary<string, string> AppEnvironment(WriteMode mode, string? rabbitMqUri = null, bool publisherEnabled = true)
    {
        var o = Options(mode);
        return new()
        {
            ["E01_MODE"] = mode.ToString().ToLowerInvariant(),
            ["E01_POSTGRES"] = o.PostgresConnectionString,
            ["E01_RABBITMQ_URI"] = rabbitMqUri ?? o.RabbitMqUri,
            ["E01_FAULT_INJECTION_ENABLED"] = "true",
            ["E01_OUTBOX_PUBLISHER_ENABLED"] = publisherEnabled.ToString().ToLowerInvariant(),
            ["E01_EXCHANGE"] = o.Exchange,
            ["E01_EVIDENCE_QUEUE"] = o.EvidenceQueue,
        };
    }

    public async ValueTask InitializeAsync()
    {
        var admin = new NpgsqlConnectionStringBuilder(Env("E01_TEST_POSTGRES_ADMIN", "Host=localhost;Username=lab;Password=lab;Database=postgres"));
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
        await SchemaInitializer.ApplyAsync(Db, NullLogger.Instance);
    }

    public async ValueTask DisposeAsync() => await Db.DisposeAsync();

    /// <summary>Empty tables and the evidence queue (declaring the topology if needed).</summary>
    public async Task ResetAsync()
    {
        await Execute("TRUNCATE work_orders, outbox_messages");
        await WithChannel(async ch =>
        {
            await Broker.DeclareTopologyAsync(ch, Options(WriteMode.Outbox), default);
            await ch.QueuePurgeAsync(Options(WriteMode.Outbox).EvidenceQueue);
        });
    }

    public Task<uint> QueueMessageCountAsync() =>
        WithChannel(ch => ch.MessageCountAsync(Options(WriteMode.Outbox).EvidenceQueue));

    /// <summary>Consumes (acks) everything currently in the evidence queue.</summary>
    public Task<List<ReceivedMessage>> DrainQueueAsync() => WithChannel(async ch =>
    {
        var messages = new List<ReceivedMessage>();
        while (await ch.BasicGetAsync(Options(WriteMode.Outbox).EvidenceQueue, autoAck: true) is { } msg)
            messages.Add(new ReceivedMessage(msg.BasicProperties.MessageId, msg.BasicProperties.Type,
                msg.BasicProperties.CorrelationId, Encoding.UTF8.GetString(msg.Body.Span)));
        return messages;
    });

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

    public async Task<List<OutboxRow>> OutboxRowsAsync()
    {
        var rows = new List<OutboxRow>();
        await using var cmd = Db.CreateCommand(
            "SELECT event_id, event_type, aggregate_id, operation_id, payload::text, published_at FROM outbox_messages ORDER BY created_at");
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(new OutboxRow(reader.GetGuid(0), reader.GetString(1), reader.GetGuid(2), reader.GetString(3),
                reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5)));
        return rows;
    }

    public async Task<List<(Guid Id, string OperationId, string WriteMode)>> WorkOrdersAsync()
    {
        var rows = new List<(Guid, string, string)>();
        await using var cmd = Db.CreateCommand("SELECT id, operation_id, write_mode FROM work_orders ORDER BY created_at");
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add((reader.GetGuid(0), reader.GetString(1), reader.GetString(2)));
        return rows;
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

public sealed record OutboxRow(Guid EventId, string EventType, Guid AggregateId, string OperationId, string Payload, DateTimeOffset? PublishedAt);

public sealed record ReceivedMessage(string? MessageId, string? Type, string? CorrelationId, string Body);
