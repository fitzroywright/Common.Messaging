namespace Common.Messaging;

public sealed class ExternalDeliveryDispatcher : IRetryableExternalDeliveryDispatcher
{
    private readonly IReadOnlyList<IExternalMessageChannel> channels;
    private readonly ExternalDeliveryOptions options;
    private readonly IExternalDeliveryFailureSink? failureSink;
    private readonly IExternalDeliverySuccessSink? successSink;
    private readonly IExternalDeliveryIdempotencyStore idempotencyStore;

    public ExternalDeliveryDispatcher(
        IEnumerable<IExternalMessageChannel> channels,
        ExternalDeliveryOptions? options = null,
        IExternalDeliveryFailureSink? failureSink = null,
        IExternalDeliverySuccessSink? successSink = null,
        IExternalDeliveryIdempotencyStore? idempotencyStore = null)
    {
        this.channels = channels?.ToArray() ?? throw new ArgumentNullException(nameof(channels));
        this.options = options ?? new ExternalDeliveryOptions();
        this.failureSink = failureSink;
        this.successSink = successSink;
        this.idempotencyStore = idempotencyStore ?? new InMemoryExternalDeliveryIdempotencyStore();
    }

    public async Task DeliverAsync(ExternalDeliveryWorkItem item, CancellationToken cancellationToken = default)
    {
        _ = await DeliverWithResultAsync(item, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ExternalDeliveryAttemptResult> DeliverWithResultAsync(
        ExternalDeliveryWorkItem item,
        CancellationToken cancellationToken = default)
    {
        List<(RecipientSnapshot Recipient, IExternalMessageChannel Channel)> work = [];
        foreach (RecipientSnapshot recipient in item.Recipients)
        {
            foreach (IExternalMessageChannel channel in channels)
            {
                if (ShouldSend(channel, recipient, item.RequestedChannels))
                {
                    work.Add((recipient, channel));
                }
            }
        }

        int delivered = 0;
        int failed = 0;
        string? lastError = null;

        if (options.DeliveryMode == ExternalDeliveryMode.Sequential)
        {
            foreach ((RecipientSnapshot recipient, IExternalMessageChannel channel) in work)
            {
                DeliveryLegResult result = await DeliverOneAsync(channel, recipient, item, cancellationToken).ConfigureAwait(false);
                delivered += result.Delivered ? 1 : 0;
                failed += result.Failed ? 1 : 0;
                lastError ??= result.ErrorCode;
            }
        }
        else
        {
            int maxConcurrency = Math.Max(1, options.MaxConcurrency);
            object gate = new();
            await Parallel.ForEachAsync(
                work,
                new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency, CancellationToken = cancellationToken },
                async (entry, token) =>
                {
                    DeliveryLegResult result = await DeliverOneAsync(entry.Channel, entry.Recipient, item, token).ConfigureAwait(false);
                    lock (gate)
                    {
                        delivered += result.Delivered ? 1 : 0;
                        failed += result.Failed ? 1 : 0;
                        lastError ??= result.ErrorCode;
                    }
                }).ConfigureAwait(false);
        }

        return new ExternalDeliveryAttemptResult(delivered, failed, lastError);
    }

    private async Task<DeliveryLegResult> DeliverOneAsync(
        IExternalMessageChannel channel,
        RecipientSnapshot recipient,
        ExternalDeliveryWorkItem item,
        CancellationToken cancellationToken)
    {
        if (await idempotencyStore.HasDeliveredAsync(item.NotificationId, recipient.UserId, channel.Channel, cancellationToken).ConfigureAwait(false))
        {
            return new DeliveryLegResult(false, false, null);
        }

        try
        {
            MessageRecipient resolved = new(
                recipient.UserId,
                recipient.UserName,
                recipient.Email,
                recipient.SlackUserId,
                recipient.Mobile,
                recipient.PreferredChannels);

            await channel.SendAsync(resolved, item.Request, item.NotificationId, cancellationToken).ConfigureAwait(false);
            await idempotencyStore.MarkDeliveredAsync(item.NotificationId, recipient.UserId, channel.Channel, cancellationToken).ConfigureAwait(false);

            if (successSink is not null)
            {
                await successSink.RecordAsync(
                    new ExternalDeliverySuccess(item.NotificationId, recipient.UserId, channel.Channel, DateTimeOffset.UtcNow, item.Request.CorrelationId),
                    cancellationToken).ConfigureAwait(false);
            }

            return new DeliveryLegResult(true, false, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            string errorCode = ExternalDeliveryErrorSanitizer.GetErrorCode(exception);
            if (failureSink is not null)
            {
                await failureSink.RecordAsync(
                    new ExternalDeliveryFailure(
                        item.NotificationId,
                        recipient.UserId,
                        channel.Channel,
                        errorCode,
                        DateTimeOffset.UtcNow,
                        item.Request.CorrelationId,
                        item.Attempt + 1),
                    cancellationToken).ConfigureAwait(false);
            }

            return new DeliveryLegResult(false, true, errorCode);
        }
    }

    private static bool ShouldSend(IExternalMessageChannel channel, RecipientSnapshot recipient, MessageChannel requestedChannels)
    {
        if (channel.Channel == MessageChannel.InApp || channel.Channel == MessageChannel.None) return false;
        MessageChannel effectiveRequested = requestedChannels == MessageChannel.None ? recipient.PreferredChannels : requestedChannels;
        return (effectiveRequested & channel.Channel) != 0 && (recipient.PreferredChannels & channel.Channel) != 0;
    }

    private sealed record DeliveryLegResult(bool Delivered, bool Failed, string? ErrorCode);
}
