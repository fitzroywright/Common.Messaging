namespace Common.Messaging;

public sealed class QueuedMessageService : IMessageService
{
    private readonly IMessageRecipientDirectory recipients;
    private readonly IMessageStore store;
    private readonly IExternalDeliveryQueue externalQueue;
    private readonly IMessageSignalSender? signalSender;
    private readonly string? contextId;
    private readonly string? contextName;

    public QueuedMessageService(
        IMessageRecipientDirectory recipients,
        IMessageStore store,
        IExternalDeliveryQueue externalQueue,
        IMessageSignalSender? signalSender = null,
        string? contextId = null,
        string? contextName = null)
    {
        this.recipients = recipients ?? throw new ArgumentNullException(nameof(recipients));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.externalQueue = externalQueue ?? throw new ArgumentNullException(nameof(externalQueue));
        this.signalSender = signalSender;
        this.contextId = contextId;
        this.contextName = contextName;
    }

    public async Task<MessageSendResult> SendAsync(
        MessageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Guid notificationId = Guid.NewGuid();
        if (request.RecipientIds.Count == 0)
        {
            return new MessageSendResult(notificationId, false, "No recipients were supplied.");
        }

        IReadOnlyList<MessageRecipient> resolved = await recipients.ResolveAsync(
            request.RecipientIds,
            cancellationToken);

        if (resolved.Count == 0)
        {
            return new MessageSendResult(notificationId, false, "No recipients were resolved.");
        }

        MessageRecipient[] distinctRecipients = resolved
            .GroupBy(recipient => recipient.UserId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        foreach (MessageRecipient recipient in distinctRecipients)
        {
            MessageChannel effectiveChannels = request.Channels == MessageChannel.None
                ? recipient.PreferredChannels
                : request.Channels;

            if ((effectiveChannels & MessageChannel.InApp) == 0)
            {
                continue;
            }

            await store.AddAsync(
                new MessageNotification(
                    notificationId,
                    recipient.UserId,
                    request.Title,
                    request.Body,
                    request.Severity,
                    request.Source,
                    request.CorrelationId,
                    DateTimeOffset.UtcNow),
                cancellationToken);
        }

        RecipientSnapshot[] snapshots = distinctRecipients
            .Select(recipient => new RecipientSnapshot(
                recipient.UserId,
                recipient.UserName,
                recipient.Email,
                recipient.SlackUserId,
                recipient.Mobile,
                recipient.PreferredChannels))
            .ToArray();

        MessageChannel externalChannels = request.Channels & ~MessageChannel.InApp;
        if (request.Channels == MessageChannel.None)
        {
            externalChannels = MessageChannel.None;
        }

        bool hasExternalDelivery = snapshots.Any(snapshot =>
        {
            MessageChannel requested = externalChannels == MessageChannel.None
                ? snapshot.PreferredChannels
                : externalChannels;
            return (requested & snapshot.PreferredChannels & ~MessageChannel.InApp) != MessageChannel.None;
        });

        if (hasExternalDelivery)
        {
            await externalQueue.EnqueueAsync(
                new ExternalDeliveryWorkItem(
                    notificationId,
                    snapshots,
                    externalChannels,
                    request,
                    contextId,
                    contextName,
                    DateTimeOffset.UtcNow),
                cancellationToken);
        }

        if (signalSender is not null)
        {
            await signalSender.SignalAsync(
                notificationId,
                distinctRecipients.Select(recipient => recipient.UserId).ToArray(),
                cancellationToken);
        }

        return new MessageSendResult(notificationId, true);
    }
}
