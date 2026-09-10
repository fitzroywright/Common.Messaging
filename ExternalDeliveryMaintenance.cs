using Npgsql;

namespace Common.Messaging;

public sealed class ExternalDeliveryRetentionOptions
{
    public TimeSpan CompletedMessageRetention { get; init; } = TimeSpan.FromDays(30);
    public TimeSpan DeliveryReceiptRetention { get; init; } = TimeSpan.FromDays(30);
}

public sealed record ExternalDeliveryMaintenanceResult(
    int CompletedMessagesRemoved,
    int DeliveryReceiptsRemoved);

public interface IExternalDeliveryMaintenance
{
    Task<ExternalDeliveryMaintenanceResult> PruneAsync(
        ExternalDeliveryRetentionOptions options,
        CancellationToken cancellationToken = default);
}

public sealed class FileExternalDeliveryMaintenance : IExternalDeliveryMaintenance
{
    private readonly string deliveredPath;

    public FileExternalDeliveryMaintenance(ExternalDeliveryOptions? options = null)
    {
        ExternalDeliveryOptions resolved = options ?? new ExternalDeliveryOptions();
        deliveredPath = Path.Combine(Path.GetFullPath(resolved.DurableQueuePath), "delivered");
    }

    public Task<ExternalDeliveryMaintenanceResult> PruneAsync(
        ExternalDeliveryRetentionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        Validate(options);
        if (!Directory.Exists(deliveredPath))
        {
            return Task.FromResult(new ExternalDeliveryMaintenanceResult(0, 0));
        }

        DateTime receiptCutoffUtc = DateTime.UtcNow - options.DeliveryReceiptRetention;
        int receiptsRemoved = 0;
        foreach (string path in Directory.EnumerateFiles(deliveredPath, "*.done", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.GetLastWriteTimeUtc(path) > receiptCutoffUtc) continue;

            try
            {
                File.Delete(path);
                receiptsRemoved++;
            }
            catch (FileNotFoundException)
            {
            }
        }

        return Task.FromResult(new ExternalDeliveryMaintenanceResult(0, receiptsRemoved));
    }

    private static void Validate(ExternalDeliveryRetentionOptions options)
    {
        if (options.CompletedMessageRetention < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options.CompletedMessageRetention));
        if (options.DeliveryReceiptRetention < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options.DeliveryReceiptRetention));
    }
}

public sealed class PostgreSqlExternalDeliveryMaintenance : IExternalDeliveryMaintenance, IAsyncDisposable
{
    private readonly NpgsqlDataSource dataSource;
    private readonly string queueName;

    public PostgreSqlExternalDeliveryMaintenance(string connectionString, string queueName = "default")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        dataSource = NpgsqlDataSource.Create(connectionString);
        this.queueName = queueName.Trim();
    }

    public async Task<ExternalDeliveryMaintenanceResult> PruneAsync(
        ExternalDeliveryRetentionOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.CompletedMessageRetention < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options.CompletedMessageRetention));
        if (options.DeliveryReceiptRetention < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options.DeliveryReceiptRetention));

        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using NpgsqlCommand completed = new(@"
DELETE FROM common_messaging_queue
WHERE queue_name = @queue
  AND state = 'completed'
  AND updated_at <= now() - @retention;", connection, transaction);
        completed.Parameters.AddWithValue("queue", queueName);
        completed.Parameters.AddWithValue("retention", options.CompletedMessageRetention);
        int completedRemoved = await completed.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using NpgsqlCommand receipts = new(@"
DELETE FROM common_messaging_delivery_receipts
WHERE queue_name = @queue
  AND delivered_at <= now() - @retention;", connection, transaction);
        receipts.Parameters.AddWithValue("queue", queueName);
        receipts.Parameters.AddWithValue("retention", options.DeliveryReceiptRetention);
        int receiptsRemoved = await receipts.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ExternalDeliveryMaintenanceResult(completedRemoved, receiptsRemoved);
    }

    public ValueTask DisposeAsync() => dataSource.DisposeAsync();
}
