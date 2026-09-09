using Xunit;

namespace Common.Messaging.UnitTests;

public sealed class DurableDeliveryHardeningTests
{
    [Fact]
    public async Task FileQueue_SurvivesProviderRecreation()
    {
        string path = Path.Combine(Path.GetTempPath(), "common-messaging-tests", Guid.NewGuid().ToString("N"));
        ExternalDeliveryOptions options = new() { DurableQueuePath = path, LeaseDuration = TimeSpan.FromSeconds(1) };
        Guid id = Guid.NewGuid();
        ExternalDeliveryWorkItem item = CreateWorkItem(id);
        try
        {
            FileExternalDeliveryQueue first = new(options);
            await first.EnqueueAsync(item);

            FileExternalDeliveryQueue second = new(options);
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
            await foreach (ExternalDeliveryWorkItem queued in second.ReadAllAsync(timeout.Token))
            {
                Assert.Equal(id, queued.NotificationId);
                await second.CompleteAsync(id, timeout.Token);
                break;
            }

            ExternalDeliveryQueueHealth health = await second.CheckHealthAsync();
            Assert.Equal(0, health.Pending);
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    [Fact]
    public async Task Dispatcher_RetryIsIdempotentForSuccessfulLegs()
    {
        RecipientSnapshot recipient = new("user-1", "User", "u@example.com", "U1", null, MessageChannel.MsEmail | MessageChannel.Slack);
        RecordingChannel email = new(MessageChannel.MsEmail);
        FlakyChannel slack = new(MessageChannel.Slack);
        InMemoryExternalDeliveryIdempotencyStore idempotency = new();
        ExternalDeliveryDispatcher dispatcher = new([email, slack], idempotencyStore: idempotency);
        ExternalDeliveryWorkItem item = CreateWorkItem(Guid.NewGuid(), recipient, MessageChannel.MsEmail | MessageChannel.Slack);

        ExternalDeliveryAttemptResult first = await dispatcher.DeliverWithResultAsync(item);
        ExternalDeliveryAttemptResult second = await dispatcher.DeliverWithResultAsync(item with { Attempt = 1 });

        Assert.False(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.Equal(1, email.SendCount);
        Assert.Equal(2, slack.SendCount);
    }

    [Fact]
    public void ErrorSanitizer_DoesNotExposeExceptionMessage()
    {
        const string secret = "https://hooks.example/secret-token";
        Exception exception = new HttpRequestException(secret);

        string errorCode = ExternalDeliveryErrorSanitizer.GetErrorCode(exception);

        Assert.Equal("DELIVERY_HTTP_ERROR", errorCode);
        Assert.DoesNotContain(secret, errorCode, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FileQueue_DeadLetterIsReportedByHealth()
    {
        string path = Path.Combine(Path.GetTempPath(), "common-messaging-tests", Guid.NewGuid().ToString("N"));
        ExternalDeliveryOptions options = new() { DurableQueuePath = path };
        FileExternalDeliveryQueue queue = new(options);
        ExternalDeliveryWorkItem item = CreateWorkItem(Guid.NewGuid());
        try
        {
            await queue.EnqueueAsync(item);
            await queue.DeadLetterAsync(item, "DELIVERY_FAILED");

            ExternalDeliveryQueueHealth health = await queue.CheckHealthAsync();

            Assert.Equal(1, health.DeadLettered);
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    [Fact]
    public async Task DeadLetter_CanBeInspectedReplayedAndCompleted()
    {
        string path = Path.Combine(Path.GetTempPath(), "common-messaging-tests", Guid.NewGuid().ToString("N"));
        ExternalDeliveryOptions options = new() { DurableQueuePath = path, LeaseDuration = TimeSpan.FromSeconds(1) };
        FileExternalDeliveryQueue queue = new(options);
        Guid id = Guid.NewGuid();
        ExternalDeliveryWorkItem failed = CreateWorkItem(id) with { Attempt = 5 };

        try
        {
            await queue.DeadLetterAsync(failed, "DELIVERY_HTTP_ERROR");

            IReadOnlyList<ExternalDeliveryDeadLetter> deadLetters = await queue.GetDeadLettersAsync();
            ExternalDeliveryDeadLetter deadLetter = Assert.Single(deadLetters);
            Assert.Equal(id, deadLetter.Item.NotificationId);
            Assert.Equal("DELIVERY_HTTP_ERROR", deadLetter.ErrorCode);

            Assert.True(await queue.ReplayAsync(id));
            ExternalDeliveryQueueHealth afterReplay = await queue.CheckHealthAsync();
            Assert.Equal(1, afterReplay.Pending);
            Assert.Equal(0, afterReplay.DeadLettered);

            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
            await foreach (ExternalDeliveryWorkItem replayed in queue.ReadAllAsync(timeout.Token))
            {
                Assert.Equal(id, replayed.NotificationId);
                Assert.Equal(0, replayed.Attempt);
                await queue.CompleteAsync(id, timeout.Token);
                break;
            }

            ExternalDeliveryQueueHealth completed = await queue.CheckHealthAsync();
            Assert.Equal(0, completed.Pending);
            Assert.Equal(0, completed.DeadLettered);
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    [Fact]
    public async Task ReplayAll_MovesEveryDeadLetterBackToPending()
    {
        string path = Path.Combine(Path.GetTempPath(), "common-messaging-tests", Guid.NewGuid().ToString("N"));
        FileExternalDeliveryQueue queue = new(new ExternalDeliveryOptions { DurableQueuePath = path });
        try
        {
            await queue.DeadLetterAsync(CreateWorkItem(Guid.NewGuid()), "FAIL_A");
            await queue.DeadLetterAsync(CreateWorkItem(Guid.NewGuid()), "FAIL_B");

            int replayed = await queue.ReplayAllAsync();
            ExternalDeliveryQueueHealth health = await queue.CheckHealthAsync();

            Assert.Equal(2, replayed);
            Assert.Equal(2, health.Pending);
            Assert.Equal(0, health.DeadLettered);
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
    }

    private static ExternalDeliveryWorkItem CreateWorkItem(Guid id)
    {
        RecipientSnapshot recipient = new("user-1", "User", "u@example.com", null, null, MessageChannel.MsEmail);
        return CreateWorkItem(id, recipient, MessageChannel.MsEmail);
    }

    private static ExternalDeliveryWorkItem CreateWorkItem(Guid id, RecipientSnapshot recipient, MessageChannel channels)
    {
        return new ExternalDeliveryWorkItem(
            id,
            [recipient],
            channels,
            new MessageRequest { RecipientIds = [recipient.UserId], Title = "Test", Body = "Body", Channels = channels },
            null,
            null,
            DateTimeOffset.UtcNow);
    }

    private sealed class RecordingChannel(MessageChannel channel) : IExternalMessageChannel
    {
        public MessageChannel Channel { get; } = channel;
        public int SendCount { get; private set; }

        public Task SendAsync(MessageRecipient recipient, MessageRequest request, Guid notificationId, CancellationToken cancellationToken = default)
        {
            SendCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FlakyChannel(MessageChannel channel) : IExternalMessageChannel
    {
        public MessageChannel Channel { get; } = channel;
        public int SendCount { get; private set; }

        public Task SendAsync(MessageRecipient recipient, MessageRequest request, Guid notificationId, CancellationToken cancellationToken = default)
        {
            SendCount++;
            if (SendCount == 1) throw new HttpRequestException("provider secret should never escape");
            return Task.CompletedTask;
        }
    }
}
