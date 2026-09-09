namespace Common.Messaging.UnitTests;

using Common.Messaging;
using Xunit;

public sealed class PostgreSqlMultiNodeIntegrationTests
{
    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("COMMON_MESSAGING_POSTGRES")
        ?? throw new InvalidOperationException("COMMON_MESSAGING_POSTGRES must be configured for PostgreSQL integration tests.");

    [Fact]
    public async Task Two_nodes_do_not_claim_the_same_live_lease()
    {
        string queueName = $"test-{Guid.NewGuid():N}";
        ExternalDeliveryOptions options = new() { LeaseDuration = TimeSpan.FromSeconds(5) };
        await using PostgreSqlExternalDeliveryStore nodeA = new(ConnectionString, options, queueName);
        await using PostgreSqlExternalDeliveryStore nodeB = new(ConnectionString, options, queueName);
        ExternalDeliveryWorkItem item = CreateItem();

        await nodeA.EnqueueAsync(item);

        await using IAsyncEnumerator<ExternalDeliveryWorkItem> readerA = nodeA.ReadAllAsync().GetAsyncEnumerator();
        Assert.True(await readerA.MoveNextAsync());
        Assert.Equal(item.NotificationId, readerA.Current.NotificationId);

        using CancellationTokenSource timeout = new(TimeSpan.FromMilliseconds(750));
        await using IAsyncEnumerator<ExternalDeliveryWorkItem> readerB = nodeB.ReadAllAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await readerB.MoveNextAsync().AsTask());

        await nodeA.CompleteAsync(item.NotificationId);
        ExternalDeliveryQueueHealth health = await nodeB.CheckHealthAsync();
        Assert.Equal(0, health.Pending);
    }

    [Fact]
    public async Task Expired_lease_is_recovered_by_another_node()
    {
        string queueName = $"test-{Guid.NewGuid():N}";
        ExternalDeliveryOptions options = new() { LeaseDuration = TimeSpan.FromMilliseconds(400) };
        await using PostgreSqlExternalDeliveryStore nodeA = new(ConnectionString, options, queueName);
        await using PostgreSqlExternalDeliveryStore nodeB = new(ConnectionString, options, queueName);
        ExternalDeliveryWorkItem item = CreateItem();

        await nodeA.EnqueueAsync(item);
        await using IAsyncEnumerator<ExternalDeliveryWorkItem> readerA = nodeA.ReadAllAsync().GetAsyncEnumerator();
        Assert.True(await readerA.MoveNextAsync());
        Assert.Equal(item.NotificationId, readerA.Current.NotificationId);

        await Task.Delay(TimeSpan.FromMilliseconds(650));

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
        await using IAsyncEnumerator<ExternalDeliveryWorkItem> readerB = nodeB.ReadAllAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert.True(await readerB.MoveNextAsync());
        Assert.Equal(item.NotificationId, readerB.Current.NotificationId);
        await nodeB.CompleteAsync(item.NotificationId, timeout.Token);
    }

    [Fact]
    public async Task Retry_dead_letter_replay_and_idempotency_are_shared_between_nodes()
    {
        string queueName = $"test-{Guid.NewGuid():N}";
        await using PostgreSqlExternalDeliveryStore nodeA = new(ConnectionString, queueName: queueName);
        await using PostgreSqlExternalDeliveryStore nodeB = new(ConnectionString, queueName: queueName);
        ExternalDeliveryWorkItem item = CreateItem();

        await nodeA.EnqueueAsync(item);
        await using (IAsyncEnumerator<ExternalDeliveryWorkItem> reader = nodeA.ReadAllAsync().GetAsyncEnumerator())
        {
            Assert.True(await reader.MoveNextAsync());
            await nodeA.RetryAsync(reader.Current, TimeSpan.Zero);
        }

        await using (IAsyncEnumerator<ExternalDeliveryWorkItem> reader = nodeB.ReadAllAsync().GetAsyncEnumerator())
        {
            Assert.True(await reader.MoveNextAsync());
            Assert.Equal(1, reader.Current.Attempt);
            await nodeB.DeadLetterAsync(reader.Current, "TEST_FAILURE");
        }

        IReadOnlyList<ExternalDeliveryDeadLetter> dead = await nodeA.GetDeadLettersAsync();
        Assert.Contains(dead, entry => entry.Item.NotificationId == item.NotificationId && entry.ErrorCode == "TEST_FAILURE");
        Assert.True(await nodeA.ReplayAsync(item.NotificationId));

        await using (IAsyncEnumerator<ExternalDeliveryWorkItem> reader = nodeB.ReadAllAsync().GetAsyncEnumerator())
        {
            Assert.True(await reader.MoveNextAsync());
            Assert.Equal(item.NotificationId, reader.Current.NotificationId);
            await nodeB.CompleteAsync(item.NotificationId);
        }

        const string recipient = "employee-1";
        await nodeA.MarkDeliveredAsync(item.NotificationId, recipient, MessageChannel.Smtp);
        Assert.True(await nodeB.HasDeliveredAsync(item.NotificationId, recipient, MessageChannel.Smtp));
        await nodeB.MarkDeliveredAsync(item.NotificationId, recipient, MessageChannel.Smtp);
        Assert.True(await nodeA.HasDeliveredAsync(item.NotificationId, recipient, MessageChannel.Smtp));
    }

    private static ExternalDeliveryWorkItem CreateItem()
    {
        Guid id = Guid.NewGuid();
        MessageRequest request = new()
        {
            RecipientIds = ["employee-1"],
            Title = "Integration test",
            Body = "PostgreSQL multi-node durability",
            Channels = MessageChannel.Smtp,
            Source = "Common.Messaging.Tests",
            CorrelationId = id.ToString("N")
        };

        return new ExternalDeliveryWorkItem
        (
            id,
            [new RecipientSnapshot("employee-1", "Employee One", "employee@example.test", null, null, MessageChannel.Smtp)],
            MessageChannel.Smtp,
            request,
            "integration-test",
            "PostgreSQL",
            DateTimeOffset.UtcNow
        );
    }
}
