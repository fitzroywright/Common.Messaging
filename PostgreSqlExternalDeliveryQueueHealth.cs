using Npgsql;

namespace Common.Messaging;

public sealed class PostgreSqlExternalDeliveryQueueHealth : IExternalDeliveryQueueHealth, IAsyncDisposable
{
    private readonly NpgsqlDataSource dataSource;
    private readonly string queueName;

    public PostgreSqlExternalDeliveryQueueHealth(string connectionString, string queueName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        dataSource = NpgsqlDataSource.Create(connectionString);
        this.queueName = queueName.Trim();
    }

    public async Task<ExternalDeliveryQueueHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using NpgsqlCommand command = dataSource.CreateCommand(@"
SELECT
    COUNT(*) FILTER (WHERE state IN ('pending','leased')) AS pending,
    COUNT(*) FILTER (WHERE state = 'dead') AS dead,
    COUNT(*) FILTER (WHERE state = 'pending' AND next_attempt_at <= now()) AS ready,
    COUNT(*) FILTER (WHERE state = 'leased' AND lease_expires_at > now()) AS leased,
    COUNT(*) FILTER (WHERE state = 'pending' AND attempt > 0) AS retrying,
    COUNT(*) FILTER (WHERE state = 'leased' AND lease_expires_at <= now()) AS expired_leases,
    MIN(created_at) FILTER (WHERE state IN ('pending','leased')) AS oldest_created
FROM common_messaging_queue
WHERE queue_name = @queue;");
            command.Parameters.AddWithValue("queue", queueName);

            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

            long pending = reader.GetInt64(0);
            long dead = reader.GetInt64(1);
            long ready = reader.GetInt64(2);
            long leased = reader.GetInt64(3);
            long retrying = reader.GetInt64(4);
            long expiredLeases = reader.GetInt64(5);
            TimeSpan? oldestPendingAge = reader.IsDBNull(6)
                ? null
                : DateTimeOffset.UtcNow - reader.GetFieldValue<DateTimeOffset>(6);

            return new ExternalDeliveryQueueHealth(
                true,
                pending,
                dead,
                "PostgreSQL durable queue metrics are available.",
                ready,
                leased,
                retrying,
                expiredLeases,
                oldestPendingAge,
                queueName);
        }
        catch (PostgresException exception) when (exception.SqlState == "42P01")
        {
            return new ExternalDeliveryQueueHealth(true, 0, 0, "PostgreSQL messaging schema has not been initialized yet.", QueueName: queueName);
        }
        catch
        {
            return new ExternalDeliveryQueueHealth(false, 0, 0, "PostgreSQL durable queue metrics are unavailable.", QueueName: queueName);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await dataSource.DisposeAsync().ConfigureAwait(false);
    }
}
