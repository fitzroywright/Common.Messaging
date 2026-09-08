using Xunit;

namespace Common.Messaging.UnitTests;

public sealed class QueuedDeliveryTests
{
    [Fact]
    public async Task QueuedMessageService_PersistsInApp_QueuesExternal_AndSignals()
    {
        MessageRecipient recipient = new(
            "user-1",
            "User One",
            Email: "user1@example.com",
            PreferredChannels: MessageChannel.InApp | MessageChannel.MsEmail);
        var directory = new StubDirectory([recipient]);
        var store = new RecordingStore();
        var queue = new RecordingQueue();
        var signal = new RecordingSignalSender();
        var sut = new QueuedMessageService(directory, store, queue, signal, "tenant-1", "Tenant One");

        MessageSendResult result = await sut.SendAsync(new MessageRequest
        {
            RecipientIds = [recipient.UserId],
            Title = "Queued",
            Body = "Body",
            Channels = MessageChannel.InApp | MessageChannel.MsEmail
        });

        Assert.True(result.Succeeded);
        Assert.Single(store.Notifications);
        ExternalDeliveryWorkItem item = Assert.Single(queue.Items);
        Assert.Equal(result.NotificationId, item.NotificationId);
        Assert.Equal("tenant-1", item.ContextId);
        Assert.Equal("Tenant One", item.ContextName);
        Assert.Equal(MessageChannel.MsEmail, item.RequestedChannels);
        RecipientSnapshot snapshot = Assert.Single(item.Recipients);
        Assert.Equal(recipient.Email, snapshot.Email);
        Assert.Equal(result.NotificationId, signal.NotificationId);
        Assert.Contains(recipient.UserId, signal.Recipients);
    }

    [Fact]
    public async Task Dispatcher_IsolatesChannelFailure_AndContinuesOtherChannels()
    {
        RecipientSnapshot recipient = new(
            "user-1",
            "User One",
            "user1@example.com",
            "U1",
            null,
            MessageChannel.MsEmail | MessageChannel.Slack);
        var failingEmail = new RecordingChannel(MessageChannel.MsEmail, shouldFail: true);
        var slack = new RecordingChannel(MessageChannel.Slack);
        var failures = new RecordingFailureSink();
        var dispatcher = new ExternalDeliveryDispatcher(
            [failingEmail, slack],
            new ExternalDeliveryOptions { DeliveryMode = ExternalDeliveryMode.Sequential },
            failures);
        ExternalDeliveryWorkItem item = CreateWorkItem(recipient, MessageChannel.MsEmail | MessageChannel.Slack);

        await dispatcher.DeliverAsync(item);

        Assert.Single(failingEmail.Sends);
        Assert.Single(slack.Sends);
        ExternalDeliveryFailure failure = Assert.Single(failures.Failures);
        Assert.Equal(MessageChannel.MsEmail, failure.Channel);
        Assert.Equal(recipient.UserId, failure.RecipientUserId);
    }

    [Fact]
    public async Task Dispatcher_RespectsRecipientChannelPreferences()
    {
        RecipientSnapshot recipient = new(
            "user-1",
            "User One",
            "user1@example.com",
            "U1",
            null,
            MessageChannel.Slack);
        var email = new RecordingChannel(MessageChannel.MsEmail);
        var slack = new RecordingChannel(MessageChannel.Slack);
        var dispatcher = new ExternalDeliveryDispatcher([email, slack]);
        ExternalDeliveryWorkItem item = CreateWorkItem(recipient, MessageChannel.MsEmail | MessageChannel.Slack);

        await dispatcher.DeliverAsync(item);

        Assert.Empty(email.Sends);
        Assert.Single(slack.Sends);
    }

    [Theory]
    [InlineData(ExternalDeliveryMode.Sequential)]
    [InlineData(ExternalDeliveryMode.Parallel)]
    [InlineData(ExternalDeliveryMode.ParallelByRecipient)]
    public async Task Dispatcher_SupportsAllDeliveryModes(ExternalDeliveryMode mode)
    {
        RecipientSnapshot first = new("user-1", "User One", "one@example.com", null, null, MessageChannel.MsEmail);
        RecipientSnapshot second = new("user-2", "User Two", "two@example.com", null, null, MessageChannel.MsEmail);
        var email = new RecordingChannel(MessageChannel.MsEmail);
        var dispatcher = new ExternalDeliveryDispatcher(
            [email],
            new ExternalDeliveryOptions { DeliveryMode = mode, MaxConcurrency = 2 });
        ExternalDeliveryWorkItem item = new(
            Guid.NewGuid(),
            [first, second],
            MessageChannel.MsEmail,
            new MessageRequest { RecipientIds = [first.UserId, second.UserId], Channels = MessageChannel.MsEmail },
            null,
            null,
            DateTimeOffset.UtcNow);

        await dispatcher.DeliverAsync(item);

        Assert.Equal(2, email.Sends.Count);
    }

    private static ExternalDeliveryWorkItem CreateWorkItem(
        RecipientSnapshot recipient,
        MessageChannel channels)
    {
        return new ExternalDeliveryWorkItem(
            Guid.NewGuid(),
            [recipient],
            channels,
            new MessageRequest
            {
                RecipientIds = [recipient.UserId],
                Title = "Test",
                Channels = channels
            },
            null,
            null,
            DateTimeOffset.UtcNow);
    }

    private sealed class StubDirectory(IReadOnlyList<MessageRecipient> recipients) : IMessageRecipientDirectory
    {
        public Task<IReadOnlyList<MessageRecipient>> ResolveAsync(
            IReadOnlyCollection<string> recipientIds,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(recipients);
        }
    }

    private sealed class RecordingStore : IMessageStore
    {
        public List<MessageNotification> Notifications { get; } = [];

        public Task AddAsync(MessageNotification notification, CancellationToken cancellationToken = default)
        {
            Notifications.Add(notification);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<MessageNotification>> GetActiveAsync(
            string recipientUserId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<MessageNotification>>([]);
        }

        public Task DismissAsync(
            Guid notificationId,
            string recipientUserId,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingQueue : IExternalDeliveryQueue
    {
        public List<ExternalDeliveryWorkItem> Items { get; } = [];

        public ValueTask EnqueueAsync(
            ExternalDeliveryWorkItem item,
            CancellationToken cancellationToken = default)
        {
            Items.Add(item);
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<ExternalDeliveryWorkItem> ReadAllAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (ExternalDeliveryWorkItem item in Items)
            {
                yield return item;
                await Task.Yield();
            }
        }
    }

    private sealed class RecordingSignalSender : IMessageSignalSender
    {
        public Guid NotificationId { get; private set; }
        public IReadOnlyCollection<string> Recipients { get; private set; } = [];

        public Task SignalAsync(
            Guid notificationId,
            IReadOnlyCollection<string> recipientUserIds,
            CancellationToken cancellationToken = default)
        {
            NotificationId = notificationId;
            Recipients = recipientUserIds;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingFailureSink : IExternalDeliveryFailureSink
    {
        public List<ExternalDeliveryFailure> Failures { get; } = [];

        public Task RecordAsync(
            ExternalDeliveryFailure failure,
            CancellationToken cancellationToken = default)
        {
            Failures.Add(failure);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingChannel(MessageChannel channel, bool shouldFail = false) : IExternalMessageChannel
    {
        public MessageChannel Channel { get; } = channel;
        public List<string> Sends { get; } = [];

        public Task SendAsync(
            MessageRecipient recipient,
            MessageRequest request,
            Guid notificationId,
            CancellationToken cancellationToken = default)
        {
            Sends.Add(recipient.UserId);
            if (shouldFail)
            {
                throw new InvalidOperationException("Simulated channel failure.");
            }

            return Task.CompletedTask;
        }
    }
}
