namespace Common.Messaging;

public sealed class MessageService : IMessageService
{
    private readonly IMessageRecipientDirectory recipients;
    private readonly IMessageStore store;
    private readonly IReadOnlyList<IExternalMessageChannel> externalChannels;

    public MessageService(
        IMessageRecipientDirectory recipients,
        IMessageStore store,
        IEnumerable<IExternalMessageChannel> externalChannels)
    {
        this.recipients = recipients;
        this.store = store;
        this.externalChannels = externalChannels.ToArray();
    }

    public async Task<MessageSendResult> SendAsync(MessageRequest request, CancellationToken cancellationToken = default)
    {
        Guid notificationId = Guid.NewGuid();
        if (request.RecipientIds.Count == 0)
        {
            return new MessageSendResult(notificationId, false, "No recipients were supplied.");
        }

        IReadOnlyList<MessageRecipient> resolved = await recipients.ResolveAsync(request.RecipientIds, cancellationToken);
        if (resolved.Count == 0)
        {
            return new MessageSendResult(notificationId, false, "No recipients were resolved.");
        }

        foreach (MessageRecipient recipient in resolved)
        {
            MessageChannel channels = request.Channels == MessageChannel.None
                ? recipient.PreferredChannels
                : request.Channels;

            if ((channels & MessageChannel.InApp) != 0)
            {
                await store.AddAsync(new MessageNotification(
                    notificationId,
                    recipient.UserId,
                    request.Title,
                    request.Body,
                    request.Severity,
                    request.Source,
                    request.CorrelationId,
                    DateTimeOffset.UtcNow), cancellationToken);
            }

            foreach (IExternalMessageChannel channel in externalChannels)
            {
                if ((channels & channel.Channel) != 0)
                {
                    await channel.SendAsync(recipient, request, notificationId, cancellationToken);
                }
            }
        }

        return new MessageSendResult(notificationId, true);
    }
}
