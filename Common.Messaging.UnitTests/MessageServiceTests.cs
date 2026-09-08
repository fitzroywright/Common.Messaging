using Xunit;

namespace Common.Messaging.UnitTests;

public sealed class MessageServiceTests
{
    [Fact]
    public async Task SendAsync_ReturnsFailure_WhenNoRecipientsSupplied()
    {
        var directory = new RecordingRecipientDirectory([]);
        var store = new RecordingStore();
        var channel = new RecordingChannel(MessageChannel.MsEmail);
        var sut = new MessageService(directory, store, [channel]);

        MessageSendResult result = await sut.SendAsync(new MessageRequest());

        Assert.False(result.Succeeded);
        Assert.Equal("No recipients were supplied.", result.Error);
        Assert.Equal(0, directory.ResolveCalls);
        Assert.Empty(store.Notifications);
        Assert.Empty(channel.Sends);
    }

    [Fact]
    public async Task SendAsync_ReturnsFailure_WhenRecipientsCannotBeResolved()
    {
        var directory = new RecordingRecipientDirectory([]);
        var store = new RecordingStore();
        var channel = new RecordingChannel(MessageChannel.MsEmail);
        var sut = new MessageService(directory, store, [channel]);

        MessageSendResult result = await sut.SendAsync(new MessageRequest
        {
            RecipientIds = ["missing-user"],
            Title = "Test"
        });

        Assert.False(result.Succeeded);
        Assert.Equal("No recipients were resolved.", result.Error);
        Assert.Equal(1, directory.ResolveCalls);
        Assert.Empty(store.Notifications);
        Assert.Empty(channel.Sends);
    }

    [Fact]
    public async Task SendAsync_UsesRecipientPreferredChannels_WhenRequestChannelsAreNone()
    {
        MessageRecipient recipient = new(
            "user-1",
            "User One",
            Email: "user1@example.com",
            PreferredChannels: MessageChannel.InApp | MessageChannel.MsEmail);
        var directory = new RecordingRecipientDirectory([recipient]);
        var store = new RecordingStore();
        var email = new RecordingChannel(MessageChannel.MsEmail);
        var slack = new RecordingChannel(MessageChannel.Slack);
        var sut = new MessageService(directory, store, [email, slack]);

        MessageSendResult result = await sut.SendAsync(new MessageRequest
        {
            RecipientIds = [recipient.UserId],
            Title = "Preferred",
            Body = "Use recipient preferences",
            Channels = MessageChannel.None
        });

        Assert.True(result.Succeeded);
        MessageNotification notification = Assert.Single(store.Notifications);
        Assert.Equal(result.NotificationId, notification.Id);
        Assert.Equal(recipient.UserId, notification.RecipientUserId);
        Assert.Single(email.Sends);
        Assert.Empty(slack.Sends);
    }

    [Fact]
    public async Task SendAsync_UsesExplicitChannels_InsteadOfRecipientPreferences()
    {
        MessageRecipient recipient = new(
            "user-1",
            "User One",
            PreferredChannels: MessageChannel.Slack);
        var directory = new RecordingRecipientDirectory([recipient]);
        var store = new RecordingStore();
        var email = new RecordingChannel(MessageChannel.MsEmail);
        var slack = new RecordingChannel(MessageChannel.Slack);
        var sut = new MessageService(directory, store, [email, slack]);

        MessageSendResult result = await sut.SendAsync(new MessageRequest
        {
            RecipientIds = [recipient.UserId],
            Title = "Explicit",
            Body = "Explicit channels win",
            Channels = MessageChannel.InApp | MessageChannel.MsEmail
        });

        Assert.True(result.Succeeded);
        Assert.Single(store.Notifications);
        RecordingSend send = Assert.Single(email.Sends);
        Assert.Equal(result.NotificationId, send.NotificationId);
        Assert.Equal(recipient.UserId, send.Recipient.UserId);
        Assert.Empty(slack.Sends);
    }

    [Fact]
    public async Task SendAsync_DispatchesToEachResolvedRecipient()
    {
        MessageRecipient first = new("user-1", "User One", PreferredChannels: MessageChannel.MsEmail);
        MessageRecipient second = new("user-2", "User Two", PreferredChannels: MessageChannel.MsEmail);
        var directory = new RecordingRecipientDirectory([first, second]);
        var store = new RecordingStore();
        var email = new RecordingChannel(MessageChannel.MsEmail);
        var sut = new MessageService(directory, store, [email]);

        MessageSendResult result = await sut.SendAsync(new MessageRequest
        {
            RecipientIds = [first.UserId, second.UserId],
            Title = "Broadcast",
            Channels = MessageChannel.MsEmail
        });

        Assert.True(result.Succeeded);
        Assert.Equal(2, email.Sends.Count);
        Assert.All(email.Sends, send => Assert.Equal(result.NotificationId, send.NotificationId));
    }

    private sealed class RecordingRecipientDirectory(IReadOnlyList<MessageRecipient> recipients) : IMessageRecipientDirectory
    {
        public int ResolveCalls { get; private set; }

        public Task<IReadOnlyList<MessageRecipient>> ResolveAsync(
            IReadOnlyCollection<string> recipientIds,
            CancellationToken cancellationToken = default)
        {
            ResolveCalls++;
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
            IReadOnlyList<MessageNotification> result = Notifications
                .Where(notification => notification.RecipientUserId == recipientUserId && !notification.IsDismissed)
                .ToArray();
            return Task.FromResult(result);
        }

        public Task DismissAsync(
            Guid notificationId,
            string recipientUserId,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingChannel(MessageChannel channel) : IExternalMessageChannel
    {
        public MessageChannel Channel { get; } = channel;
        public List<RecordingSend> Sends { get; } = [];

        public Task SendAsync(
            MessageRecipient recipient,
            MessageRequest request,
            Guid notificationId,
            CancellationToken cancellationToken = default)
        {
            Sends.Add(new RecordingSend(recipient, request, notificationId));
            return Task.CompletedTask;
        }
    }

    private sealed record RecordingSend(
        MessageRecipient Recipient,
        MessageRequest Request,
        Guid NotificationId);
}
