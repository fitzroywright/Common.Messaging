namespace Common.Messaging;

public sealed class MessageService : IMessageService
{
    private readonly IMessageRecipientDirectory recipientDirectory;
    private readonly IMessageStore store;
    private readonly IReadOnlyList<IExternalMessageChannel> externalChannels;
    private readonly string? contextId;
    private readonly string? contextName;

    public MessageService(
        IMessageRecipientDirectory recipientDirectory,
        IMessageStore store,
        IEnumerable<IExternalMessageChannel>? externalChannels = null,
        string? contextId = null,
        string? contextName = null)
    {
        this.recipientDirectory = recipientDirectory ?? throw new ArgumentNullException(nameof(recipientDirectory));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.externalChannels = externalChannels?.ToArray() ?? [];
        this.contextId = contextId;
        this.contextName = contextName;
    }

    public async Task<MessageSendResult> SendAsync(
        MessageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.RecipientIds.Count == 0)
        {
            return new MessageSendResult(Guid.Empty, false, "At least one recipient is required.");
        }

        IReadOnlyList<MessageRecipient> recipients = await recipientDirectory
            .ResolveAsync(request.RecipientIds, cancellationToken)
            .ConfigureAwait(false);

        if (recipients.Count == 0)
        {
            return new MessageSendResult(Guid.Empty, false, "No recipients could be resolved.");
        }

        Guid notificationId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (MessageRecipient recipient in recipients)
        {
            if ((request.Channels & MessageChannel.InApp) != 0)
            {
                MessageNotification notification = new(
                    notificationId,
                    recipient.UserId,
                    request.Title,
                    request.Body,
                    request.Severity,
                    now,
                    request.ExpiresAt,
                    request.CorrelationId,
                    contextId,
                    contextName,
                    request.Metadata);
                await store.AddAsync(notification, cancellationToken).ConfigureAwait(false);
            }
        }

        MessageChannel externalRequested = request.Channels & ~MessageChannel.InApp;
        if (externalRequested == MessageChannel.None)
        {
            return new MessageSendResult(notificationId, true);
        }

        DeliveryOutcomeSink outcomes = new();
        ExternalDeliveryDispatcher dispatcher = new(externalChannels, failureSink: outcomes, successSink: outcomes);
        RecipientSnapshot[] snapshots = recipients
            .Select(recipient => new RecipientSnapshot(
                recipient.UserId,
                recipient.UserName,
                recipient.Email,
                recipient.SlackUserId,
                recipient.Mobile,
                recipient.PreferredChannels))
            .ToArray();

        ExternalDeliveryWorkItem workItem = new(
            notificationId,
            snapshots,
            request.Channels,
            request,
            request.Metadata is not null && request.Metadata.TryGetValue("ContextId", out string? metadataContextId)
                ? metadataContextId
                : contextId,
            request.Metadata is not null && request.Metadata.TryGetValue("ContextName", out string? metadataContextName)
                ? metadataContextName
                : contextName,
            DateTimeOffset.UtcNow);

        await dispatcher.DeliverAsync(workItem, cancellationToken).ConfigureAwait(false);

        if (outcomes.Failures.Count > 0)
        {
            string error = string.Join(
                "; ",
                outcomes.Failures.Select(failure =>
                    $"{failure.Channel} for {failure.RecipientUserId}: {failure.ErrorCode}"));
            return new MessageSendResult(notificationId, false, error);
        }

        return new MessageSendResult(notificationId, true);
    }

    private sealed class DeliveryOutcomeSink : IExternalDeliveryFailureSink, IExternalDeliverySuccessSink
    {
        public List<ExternalDeliveryFailure> Failures { get; } = [];
        public List<ExternalDeliverySuccess> Successes { get; } = [];

        public Task RecordAsync(ExternalDeliveryFailure failure, CancellationToken cancellationToken = default)
        {
            Failures.Add(failure);
            return Task.CompletedTask;
        }

        public Task RecordAsync(ExternalDeliverySuccess success, CancellationToken cancellationToken = default)
        {
            Successes.Add(success);
            return Task.CompletedTask;
        }
    }
}
