namespace Common.Messaging;

public sealed class MessageService : IMessageService
{
    private readonly IMessageRecipientDirectory recipients;
    private readonly IMessageStore store;
    private readonly IReadOnlyList<IExternalMessageChannel> externalChannels;
    private readonly ExternalDeliveryOptions deliveryOptions;

    public MessageService(
        IMessageRecipientDirectory recipients,
        IMessageStore store,
        IEnumerable<IExternalMessageChannel> externalChannels,
        ExternalDeliveryOptions? deliveryOptions = null)
    {
        this.recipients = recipients ?? throw new ArgumentNullException(nameof(recipients));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.externalChannels = externalChannels?.ToArray() ?? throw new ArgumentNullException(nameof(externalChannels));
        this.deliveryOptions = deliveryOptions ?? new ExternalDeliveryOptions
        {
            DeliveryMode = ExternalDeliveryMode.ParallelByRecipient,
            MaxConcurrency = 4
        };
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

        foreach (MessageRecipient recipient in resolved)
        {
            MessageChannel effectiveChannels = request.Channels == MessageChannel.None
                ? recipient.PreferredChannels
                : request.Channels;

            if ((effectiveChannels & MessageChannel.InApp) != 0)
            {
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
        }

        MessageChannel requestedExternalChannels = request.Channels & ~MessageChannel.InApp;
        bool hasExternalDelivery = request.Channels == MessageChannel.None
            ? resolved.Any(recipient => (recipient.PreferredChannels & ~MessageChannel.InApp) != 0)
            : requestedExternalChannels != MessageChannel.None;

        if (!hasExternalDelivery)
        {
            return new MessageSendResult(notificationId, true);
        }

        DeliveryOutcomeSink outcomes = new();
        ExternalDeliveryDispatcher dispatcher = new(
            externalChannels,
            deliveryOptions,
            outcomes,
            outcomes);

        IReadOnlyList<RecipientSnapshot> snapshots = resolved
            .Select(recipient =>
            {
                MessageChannel effectiveChannels = request.Channels == MessageChannel.None
                    ? recipient.PreferredChannels
                    : request.Channels;

                return new RecipientSnapshot(
                    recipient.UserId,
                    recipient.UserName,
                    recipient.Email,
                    recipient.SlackUserId,
                    recipient.Mobile,
                    effectiveChannels);
            })
            .ToArray();

        ExternalDeliveryWorkItem workItem = new(
            notificationId,
            snapshots,
            request.Channels,
            request,
            request.Metadata is not null && request.Metadata.TryGetValue("ContextId", out string? contextId)
                ? contextId
                : null,
            request.Metadata is not null && request.Metadata.TryGetValue("ContextName", out string? contextName)
                ? contextName
                : null,
            DateTimeOffset.UtcNow);

        await dispatcher.DeliverAsync(workItem, cancellationToken);

        if (outcomes.Failures.Count > 0)
        {
            string error = string.Join(
                "; ",
                outcomes.Failures.Select(failure =>
                    $"{failure.Channel} for {failure.RecipientUserId}: {failure.Error}"));
            return new MessageSendResult(notificationId, false, error);
        }

        return new MessageSendResult(notificationId, true);
    }

    private sealed class DeliveryOutcomeSink : IExternalDeliveryFailureSink, IExternalDeliverySuccessSink
    {
        public List<ExternalDeliveryFailure> Failures { get; } = [];
        public List<ExternalDeliverySuccess> Successes { get; } = [];

        public Task RecordAsync(
            ExternalDeliveryFailure failure,
            CancellationToken cancellationToken = default)
        {
            Failures.Add(failure);
            return Task.CompletedTask;
        }

        public Task RecordAsync(
            ExternalDeliverySuccess success,
            CancellationToken cancellationToken = default)
        {
            Successes.Add(success);
            return Task.CompletedTask;
        }
    }
}
