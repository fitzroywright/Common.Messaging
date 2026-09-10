namespace Common.Messaging.Hosting;

using Microsoft.Extensions.Configuration;

internal sealed class ConfiguredExternalDeliveryStore :
    IExternalDeliveryQueue,
    IExternalDeliveryQueueHealth,
    IExternalDeliveryDeadLetterStore,
    IExternalDeliveryIdempotencyStore,
    IExternalDeliveryMaintenance,
    IAsyncDisposable
{
    private readonly IExternalDeliveryQueue queue;
    private readonly IExternalDeliveryQueueHealth health;
    private readonly IExternalDeliveryDeadLetterStore deadLetters;
    private readonly IExternalDeliveryIdempotencyStore idempotency;
    private readonly IExternalDeliveryMaintenance maintenance;
    private readonly List<IAsyncDisposable> asyncDisposables = [];

    public ConfiguredExternalDeliveryStore(IConfiguration? configuration, ExternalDeliveryOptions options)
    {
        string provider = configuration?["CommonMessaging:Durability:Provider"]?.Trim() ?? "File";

        if (provider.Equals("PostgreSql", StringComparison.OrdinalIgnoreCase) ||
            provider.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
        {
            string connectionStringName = configuration?["CommonMessaging:Durability:ConnectionStringName"]?.Trim() ?? string.Empty;
            string connectionString = configuration?["CommonMessaging:Durability:ConnectionString"]?.Trim() ?? string.Empty;
            if (connectionString.Length == 0 && connectionStringName.Length > 0)
            {
                connectionString = configuration?.GetConnectionString(connectionStringName) ?? string.Empty;
            }

            if (connectionString.Length == 0)
            {
                throw new InvalidOperationException(
                    "Common.Messaging PostgreSQL durability is selected but no connection string was configured. " +
                    "Set CommonMessaging:Durability:ConnectionString or ConnectionStringName.");
            }

            string queueName = configuration?["CommonMessaging:Durability:QueueName"]?.Trim() ?? "default";
            PostgreSqlExternalDeliveryStore postgres = new(connectionString, options, queueName);
            PostgreSqlExternalDeliveryQueueHealth metrics = new(connectionString, queueName);
            PostgreSqlExternalDeliveryMaintenance postgresMaintenance = new(connectionString, queueName);
            this.queue = postgres;
            this.health = metrics;
            this.deadLetters = postgres;
            this.idempotency = postgres;
            this.maintenance = postgresMaintenance;
            this.asyncDisposables.Add(postgres);
            this.asyncDisposables.Add(metrics);
            this.asyncDisposables.Add(postgresMaintenance);
            return;
        }

        if (!provider.Equals("File", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Unsupported Common.Messaging durability provider '{provider}'. Expected File or PostgreSql.");
        }

        FileExternalDeliveryQueue fileQueue = new(options);
        this.queue = fileQueue;
        this.health = fileQueue;
        this.deadLetters = fileQueue;
        this.idempotency = new FileExternalDeliveryIdempotencyStore(options);
        this.maintenance = new FileExternalDeliveryMaintenance(options);
    }

    public ValueTask EnqueueAsync(ExternalDeliveryWorkItem item, CancellationToken cancellationToken = default)
        => this.queue.EnqueueAsync(item, cancellationToken);

    public IAsyncEnumerable<ExternalDeliveryWorkItem> ReadAllAsync(CancellationToken cancellationToken = default)
        => this.queue.ReadAllAsync(cancellationToken);

    public ValueTask CompleteAsync(Guid notificationId, CancellationToken cancellationToken = default)
        => this.queue.CompleteAsync(notificationId, cancellationToken);

    public ValueTask RetryAsync(ExternalDeliveryWorkItem item, TimeSpan delay, CancellationToken cancellationToken = default)
        => this.queue.RetryAsync(item, delay, cancellationToken);

    public ValueTask DeadLetterAsync(ExternalDeliveryWorkItem item, string errorCode, CancellationToken cancellationToken = default)
        => this.queue.DeadLetterAsync(item, errorCode, cancellationToken);

    public Task<ExternalDeliveryQueueHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
        => this.health.CheckHealthAsync(cancellationToken);

    public Task<IReadOnlyList<ExternalDeliveryDeadLetter>> GetDeadLettersAsync(CancellationToken cancellationToken = default)
        => this.deadLetters.GetDeadLettersAsync(cancellationToken);

    public ValueTask<bool> ReplayAsync(Guid notificationId, CancellationToken cancellationToken = default)
        => this.deadLetters.ReplayAsync(notificationId, cancellationToken);

    public ValueTask<int> ReplayAllAsync(CancellationToken cancellationToken = default)
        => this.deadLetters.ReplayAllAsync(cancellationToken);

    public ValueTask<bool> DiscardAsync(Guid notificationId, CancellationToken cancellationToken = default)
        => this.deadLetters.DiscardAsync(notificationId, cancellationToken);

    public ValueTask<int> DiscardAllAsync(CancellationToken cancellationToken = default)
        => this.deadLetters.DiscardAllAsync(cancellationToken);

    public Task<bool> HasDeliveredAsync(Guid notificationId, string recipientUserId, MessageChannel channel, CancellationToken cancellationToken = default)
        => this.idempotency.HasDeliveredAsync(notificationId, recipientUserId, channel, cancellationToken);

    public Task MarkDeliveredAsync(Guid notificationId, string recipientUserId, MessageChannel channel, CancellationToken cancellationToken = default)
        => this.idempotency.MarkDeliveredAsync(notificationId, recipientUserId, channel, cancellationToken);

    public Task<ExternalDeliveryMaintenanceResult> PruneAsync(
        ExternalDeliveryRetentionOptions options,
        CancellationToken cancellationToken = default)
        => this.maintenance.PruneAsync(options, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        foreach (IAsyncDisposable disposable in this.asyncDisposables)
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }
    }
}
