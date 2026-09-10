using System.Runtime.CompilerServices;
using System.Text.Json;

using Npgsql;
using NpgsqlTypes;

namespace Common.Messaging;

public sealed class PostgreSqlExternalDeliveryStore :
    IExternalDeliveryQueue,
    IExternalDeliveryQueueHealth,
    IExternalDeliveryDeadLetterStore,
    IExternalDeliveryIdempotencyStore,
    IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly NpgsqlDataSource dataSource;
    private readonly ExternalDeliveryOptions options;
    private readonly string queueName;
    private readonly string leaseOwner = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
    private readonly SemaphoreSlim initializeLock = new(1, 1);
    private volatile bool initialized;

    public PostgreSqlExternalDeliveryStore(string connectionString, ExternalDeliveryOptions? options = null, string queueName = "default")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        this.options = options ?? new ExternalDeliveryOptions();
        this.queueName = queueName.Trim();
        dataSource = NpgsqlDataSource.Create(connectionString);
    }

    public async ValueTask EnqueueAsync(ExternalDeliveryWorkItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        string payload = JsonSerializer.Serialize(item, JsonOptions);

        await using NpgsqlCommand command = dataSource.CreateCommand(@"
INSERT INTO common_messaging_queue
    (queue_name, notification_id, payload_json, attempt, state, next_attempt_at, created_at, updated_at)
VALUES
    (@queue, @id, @payload::jsonb, @attempt, 'pending', now(), now(), now())
ON CONFLICT (queue_name, notification_id) DO UPDATE
SET payload_json = EXCLUDED.payload_json,
    attempt = EXCLUDED.attempt,
    state = 'pending',
    next_attempt_at = now(),
    lease_owner = NULL,
    lease_expires_at = NULL,
    error_code = NULL,
    updated_at = now()
WHERE common_messaging_queue.state <> 'completed';");
        command.Parameters.AddWithValue("queue", queueName);
        command.Parameters.AddWithValue("id", item.NotificationId);
        command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, payload);
        command.Parameters.AddWithValue("attempt", item.Attempt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<ExternalDeliveryWorkItem> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);

        while (!cancellationToken.IsCancellationRequested)
        {
            ExternalDeliveryWorkItem? item = await TryLeaseNextAsync(cancellationToken).ConfigureAwait(false);
            if (item is not null)
            {
                yield return item;
                continue;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask CompleteAsync(Guid notificationId, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = dataSource.CreateCommand(@"
UPDATE common_messaging_queue
SET state = 'completed', lease_owner = NULL, lease_expires_at = NULL, updated_at = now()
WHERE queue_name = @queue AND notification_id = @id AND state = 'leased' AND lease_owner = @owner;");
        command.Parameters.AddWithValue("queue", queueName);
        command.Parameters.AddWithValue("id", notificationId);
        command.Parameters.AddWithValue("owner", leaseOwner);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask RetryAsync(ExternalDeliveryWorkItem item, TimeSpan delay, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        ExternalDeliveryWorkItem retry = item with { Attempt = item.Attempt + 1 };
        string payload = JsonSerializer.Serialize(retry, JsonOptions);
        await using NpgsqlCommand command = dataSource.CreateCommand(@"
UPDATE common_messaging_queue
SET payload_json = @payload::jsonb,
    attempt = @attempt,
    state = 'pending',
    next_attempt_at = now() + @delay,
    lease_owner = NULL,
    lease_expires_at = NULL,
    updated_at = now()
WHERE queue_name = @queue AND notification_id = @id AND state = 'leased' AND lease_owner = @owner;");
        command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, payload);
        command.Parameters.AddWithValue("attempt", retry.Attempt);
        command.Parameters.AddWithValue("delay", delay);
        command.Parameters.AddWithValue("queue", queueName);
        command.Parameters.AddWithValue("id", item.NotificationId);
        command.Parameters.AddWithValue("owner", leaseOwner);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DeadLetterAsync(ExternalDeliveryWorkItem item, string errorCode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        string payload = JsonSerializer.Serialize(item, JsonOptions);
        await using NpgsqlCommand command = dataSource.CreateCommand(@"
UPDATE common_messaging_queue
SET payload_json = @payload::jsonb,
    attempt = @attempt,
    state = 'dead',
    error_code = @error,
    dead_lettered_at = now(),
    lease_owner = NULL,
    lease_expires_at = NULL,
    updated_at = now()
WHERE queue_name = @queue AND notification_id = @id AND state = 'leased' AND lease_owner = @owner;");
        command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, payload);
        command.Parameters.AddWithValue("attempt", item.Attempt);
        command.Parameters.AddWithValue("error", errorCode);
        command.Parameters.AddWithValue("queue", queueName);
        command.Parameters.AddWithValue("id", item.NotificationId);
        command.Parameters.AddWithValue("owner", leaseOwner);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ExternalDeliveryDeadLetter>> GetDeadLettersAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        List<ExternalDeliveryDeadLetter> result = [];
        await using NpgsqlCommand command = dataSource.CreateCommand(@"
SELECT payload_json::text, COALESCE(error_code, 'DELIVERY_FAILED'), COALESCE(dead_lettered_at, updated_at)
FROM common_messaging_queue
WHERE queue_name = @queue AND state = 'dead'
ORDER BY COALESCE(dead_lettered_at, updated_at);");
        command.Parameters.AddWithValue("queue", queueName);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ExternalDeliveryWorkItem? item = JsonSerializer.Deserialize<ExternalDeliveryWorkItem>(reader.GetString(0), JsonOptions);
            if (item is not null)
            {
                result.Add(new ExternalDeliveryDeadLetter(item, reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2)));
            }
        }
        return result;
    }

    public async ValueTask<bool> ReplayAsync(Guid notificationId, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = dataSource.CreateCommand(@"
UPDATE common_messaging_queue
SET state = 'pending', attempt = 0, next_attempt_at = now(), error_code = NULL, dead_lettered_at = NULL, updated_at = now()
WHERE queue_name = @queue AND notification_id = @id AND state = 'dead';");
        command.Parameters.AddWithValue("queue", queueName);
        command.Parameters.AddWithValue("id", notificationId);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async ValueTask<int> ReplayAllAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = dataSource.CreateCommand(@"
UPDATE common_messaging_queue
SET state = 'pending', attempt = 0, next_attempt_at = now(), error_code = NULL, dead_lettered_at = NULL, updated_at = now()
WHERE queue_name = @queue AND state = 'dead';");
        command.Parameters.AddWithValue("queue", queueName);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> DiscardAsync(Guid notificationId, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = dataSource.CreateCommand(@"
DELETE FROM common_messaging_queue
WHERE queue_name = @queue AND notification_id = @id AND state = 'dead';");
        command.Parameters.AddWithValue("queue", queueName);
        command.Parameters.AddWithValue("id", notificationId);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async ValueTask<int> DiscardAllAsync(CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = dataSource.CreateCommand(@"
DELETE FROM common_messaging_queue
WHERE queue_name = @queue AND state = 'dead';");
        command.Parameters.AddWithValue("queue", queueName);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ExternalDeliveryQueueHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
            await using NpgsqlCommand command = dataSource.CreateCommand(@"
SELECT
    COUNT(*) FILTER (WHERE state IN ('pending','leased')),
    COUNT(*) FILTER (WHERE state = 'dead')
FROM common_messaging_queue
WHERE queue_name = @queue;");
            command.Parameters.AddWithValue("queue", queueName);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            return new ExternalDeliveryQueueHealth(true, reader.GetInt64(0), reader.GetInt64(1), "PostgreSQL durable queue is available for multi-node delivery.");
        }
        catch
        {
            return new ExternalDeliveryQueueHealth(false, 0, 0, "PostgreSQL durable queue is unavailable.");
        }
    }

    public async Task<bool> HasDeliveredAsync(Guid notificationId, string recipientUserId, MessageChannel channel, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = dataSource.CreateCommand(@"
SELECT EXISTS (
    SELECT 1 FROM common_messaging_delivery_receipts
    WHERE queue_name = @queue AND notification_id = @id AND recipient_user_id = @recipient AND channel = @channel);");
        command.Parameters.AddWithValue("queue", queueName);
        command.Parameters.AddWithValue("id", notificationId);
        command.Parameters.AddWithValue("recipient", recipientUserId);
        command.Parameters.AddWithValue("channel", (int)channel);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false);
    }

    public async Task MarkDeliveredAsync(Guid notificationId, string recipientUserId, MessageChannel channel, CancellationToken cancellationToken = default)
    {
        await EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = dataSource.CreateCommand(@"
INSERT INTO common_messaging_delivery_receipts
    (queue_name, notification_id, recipient_user_id, channel, delivered_at)
VALUES (@queue, @id, @recipient, @channel, now())
ON CONFLICT DO NOTHING;");
        command.Parameters.AddWithValue("queue", queueName);
        command.Parameters.AddWithValue("id", notificationId);
        command.Parameters.AddWithValue("recipient", recipientUserId);
        command.Parameters.AddWithValue("channel", (int)channel);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        initializeLock.Dispose();
        await dataSource.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<ExternalDeliveryWorkItem?> TryLeaseNextAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = new(@"
WITH candidate AS (
    SELECT id
    FROM common_messaging_queue
    WHERE queue_name = @queue
      AND state IN ('pending','leased')
      AND next_attempt_at <= now()
      AND (state = 'pending' OR lease_expires_at <= now())
    ORDER BY created_at, id
    FOR UPDATE SKIP LOCKED
    LIMIT 1
)
UPDATE common_messaging_queue q
SET state = 'leased',
    lease_owner = @owner,
    lease_expires_at = now() + @lease,
    updated_at = now()
FROM candidate
WHERE q.id = candidate.id
RETURNING q.payload_json::text;", connection, transaction);
        command.Parameters.AddWithValue("queue", queueName);
        command.Parameters.AddWithValue("owner", leaseOwner);
        command.Parameters.AddWithValue("lease", options.LeaseDuration);
        object? payload = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return payload is string json ? JsonSerializer.Deserialize<ExternalDeliveryWorkItem>(json, JsonOptions) : null;
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        if (initialized) return;
        await initializeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (initialized) return;
            await using NpgsqlCommand command = dataSource.CreateCommand(@"
CREATE TABLE IF NOT EXISTS common_messaging_queue (
    id bigserial PRIMARY KEY,
    queue_name text NOT NULL,
    notification_id uuid NOT NULL,
    payload_json jsonb NOT NULL,
    attempt integer NOT NULL DEFAULT 0,
    state text NOT NULL,
    next_attempt_at timestamptz NOT NULL,
    lease_owner text NULL,
    lease_expires_at timestamptz NULL,
    error_code text NULL,
    dead_lettered_at timestamptz NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    CONSTRAINT uq_common_messaging_queue UNIQUE (queue_name, notification_id)
);
CREATE INDEX IF NOT EXISTS ix_common_messaging_queue_ready
ON common_messaging_queue (queue_name, state, next_attempt_at, lease_expires_at);
CREATE TABLE IF NOT EXISTS common_messaging_delivery_receipts (
    queue_name text NOT NULL,
    notification_id uuid NOT NULL,
    recipient_user_id text NOT NULL,
    channel integer NOT NULL,
    delivered_at timestamptz NOT NULL,
    PRIMARY KEY (queue_name, notification_id, recipient_user_id, channel)
);");
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            initialized = true;
        }
        finally
        {
            initializeLock.Release();
        }
    }
}
