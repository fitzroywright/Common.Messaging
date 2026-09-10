namespace Common.Messaging.UnitTests;

using Common.Messaging;
using Xunit;

public sealed class ExternalDeliveryMaintenanceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "common-messaging-maintenance-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task FileMaintenance_PrunesExpiredDeliveryReceipts()
    {
        ExternalDeliveryOptions deliveryOptions = new() { DurableQueuePath = root };
        FileExternalDeliveryIdempotencyStore receipts = new(deliveryOptions);
        FileExternalDeliveryMaintenance maintenance = new(deliveryOptions);
        Guid notificationId = Guid.NewGuid();

        await receipts.MarkDeliveredAsync(notificationId, "user-1", MessageChannel.Smtp);
        Assert.True(await receipts.HasDeliveredAsync(notificationId, "user-1", MessageChannel.Smtp));

        ExternalDeliveryMaintenanceResult result = await maintenance.PruneAsync(new ExternalDeliveryRetentionOptions
        {
            CompletedMessageRetention = TimeSpan.Zero,
            DeliveryReceiptRetention = TimeSpan.Zero
        });

        Assert.Equal(0, result.CompletedMessagesRemoved);
        Assert.Equal(1, result.DeliveryReceiptsRemoved);
        Assert.False(await receipts.HasDeliveredAsync(notificationId, "user-1", MessageChannel.Smtp));
    }

    [Fact]
    public async Task FileMaintenance_PreservesReceiptsInsideRetentionWindow()
    {
        ExternalDeliveryOptions deliveryOptions = new() { DurableQueuePath = root };
        FileExternalDeliveryIdempotencyStore receipts = new(deliveryOptions);
        FileExternalDeliveryMaintenance maintenance = new(deliveryOptions);
        Guid notificationId = Guid.NewGuid();

        await receipts.MarkDeliveredAsync(notificationId, "user-1", MessageChannel.Smtp);

        ExternalDeliveryMaintenanceResult result = await maintenance.PruneAsync(new ExternalDeliveryRetentionOptions
        {
            DeliveryReceiptRetention = TimeSpan.FromDays(30)
        });

        Assert.Equal(0, result.DeliveryReceiptsRemoved);
        Assert.True(await receipts.HasDeliveredAsync(notificationId, "user-1", MessageChannel.Smtp));
    }

    [Fact]
    public async Task PostgreSqlMaintenance_PrunesCompletedRowsAndDeliveryReceipts()
    {
        string connectionString = Environment.GetEnvironmentVariable("COMMON_MESSAGING_POSTGRES")
            ?? throw new InvalidOperationException("COMMON_MESSAGING_POSTGRES must be configured for PostgreSQL integration tests.");
        string queueName = $"maintenance-{Guid.NewGuid():N}";
        ExternalDeliveryOptions deliveryOptions = new() { LeaseDuration = TimeSpan.FromSeconds(5) };
        await using PostgreSqlExternalDeliveryStore store = new(connectionString, deliveryOptions, queueName);
        await using PostgreSqlExternalDeliveryMaintenance maintenance = new(connectionString, queueName);
        ExternalDeliveryWorkItem item = CreateItem();

        await store.EnqueueAsync(item);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        await using IAsyncEnumerator<ExternalDeliveryWorkItem> reader = store.ReadAllAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert.True(await reader.MoveNextAsync());
        await store.MarkDeliveredAsync(item.NotificationId, "user-1", MessageChannel.Smtp, timeout.Token);
        await store.CompleteAsync(item.NotificationId, timeout.Token);
        Assert.True(await store.HasDeliveredAsync(item.NotificationId, "user-1", MessageChannel.Smtp, timeout.Token));

        ExternalDeliveryMaintenanceResult result = await maintenance.PruneAsync(new ExternalDeliveryRetentionOptions
        {
            CompletedMessageRetention = TimeSpan.Zero,
            DeliveryReceiptRetention = TimeSpan.Zero
        }, timeout.Token);

        Assert.Equal(1, result.CompletedMessagesRemoved);
        Assert.Equal(1, result.DeliveryReceiptsRemoved);
        Assert.False(await store.HasDeliveredAsync(item.NotificationId, "user-1", MessageChannel.Smtp, timeout.Token));
    }

    private static ExternalDeliveryWorkItem CreateItem()
        => new(
            Guid.NewGuid(),
            [new RecipientSnapshot("user-1", "Test User", "test@example.invalid", null, null, MessageChannel.Smtp)],
            MessageChannel.Smtp,
            new MessageRequest
            {
                RecipientIds = ["user-1"],
                Title = "Maintenance test",
                Body = "Body",
                Channels = MessageChannel.Smtp
            },
            null,
            null,
            DateTimeOffset.UtcNow);

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
