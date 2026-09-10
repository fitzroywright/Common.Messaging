namespace Common.Messaging.UnitTests;

using Common.Messaging;
using Xunit;

public sealed class DeadLetterAdministrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "common-messaging-deadletter-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task FileQueue_Discard_RemovesSelectedDeadLetter()
    {
        FileExternalDeliveryQueue queue = CreateFileQueue();
        ExternalDeliveryWorkItem first = CreateItem();
        ExternalDeliveryWorkItem second = CreateItem();
        await queue.DeadLetterAsync(first, "TEST-FAILURE");
        await queue.DeadLetterAsync(second, "TEST-FAILURE");

        Assert.True(await queue.DiscardAsync(first.NotificationId));
        Assert.False(await queue.DiscardAsync(first.NotificationId));

        IReadOnlyList<ExternalDeliveryDeadLetter> remaining = await queue.GetDeadLettersAsync();
        Assert.Single(remaining);
        Assert.Equal(second.NotificationId, remaining[0].Item.NotificationId);
    }

    [Fact]
    public async Task FileQueue_DiscardAll_RemovesEveryDeadLetter()
    {
        FileExternalDeliveryQueue queue = CreateFileQueue();
        await queue.DeadLetterAsync(CreateItem(), "TEST-ONE");
        await queue.DeadLetterAsync(CreateItem(), "TEST-TWO");

        int discarded = await queue.DiscardAllAsync();

        Assert.Equal(2, discarded);
        Assert.Empty(await queue.GetDeadLettersAsync());
    }

    [Fact]
    public async Task PostgreSqlQueue_DiscardAndDiscardAll_RemoveOnlyDeadLetters()
    {
        string connectionString = Environment.GetEnvironmentVariable("COMMON_MESSAGING_POSTGRES")
            ?? throw new InvalidOperationException("COMMON_MESSAGING_POSTGRES must be configured for PostgreSQL integration tests.");
        string queueName = $"deadletter-admin-{Guid.NewGuid():N}";
        await using PostgreSqlExternalDeliveryStore store = new(connectionString, queueName: queueName);

        ExternalDeliveryWorkItem first = CreateItem();
        ExternalDeliveryWorkItem second = CreateItem();
        await LeaseAndDeadLetterAsync(store, first, "TEST-ONE");
        await LeaseAndDeadLetterAsync(store, second, "TEST-TWO");

        Assert.True(await store.DiscardAsync(first.NotificationId));
        Assert.False(await store.DiscardAsync(first.NotificationId));
        Assert.Single(await store.GetDeadLettersAsync());

        Assert.Equal(1, await store.DiscardAllAsync());
        Assert.Empty(await store.GetDeadLettersAsync());
    }

    private FileExternalDeliveryQueue CreateFileQueue()
        => new(new ExternalDeliveryOptions { DurableQueuePath = root });

    private static async Task LeaseAndDeadLetterAsync(
        PostgreSqlExternalDeliveryStore store,
        ExternalDeliveryWorkItem item,
        string errorCode)
    {
        await store.EnqueueAsync(item);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        await using IAsyncEnumerator<ExternalDeliveryWorkItem> reader = store.ReadAllAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(item.NotificationId, reader.Current.NotificationId);
        await store.DeadLetterAsync(reader.Current, errorCode, timeout.Token);
    }

    private static ExternalDeliveryWorkItem CreateItem()
        => new(
            Guid.NewGuid(),
            [new RecipientSnapshot("user-1", "Test User", "test@example.invalid", null, null, MessageChannel.Email)],
            MessageChannel.Email,
            new MessageRequest("Test", "Body"),
            null,
            null,
            DateTimeOffset.UtcNow);

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
