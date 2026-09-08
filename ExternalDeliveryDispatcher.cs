namespace Common.Messaging;

public sealed class ExternalDeliveryDispatcher : IExternalDeliveryDispatcher
{
    private readonly IReadOnlyList<IExternalMessageChannel> channels;
    private readonly ExternalDeliveryOptions options;
    private readonly IExternalDeliveryFailureSink? failureSink;
    private readonly IExternalDeliverySuccessSink? successSink;

    public ExternalDeliveryDispatcher(
        IEnumerable<IExternalMessageChannel> channels,
        ExternalDeliveryOptions? options = null,
        IExternalDeliveryFailureSink? failureSink = null,
        IExternalDeliverySuccessSink? successSink = null)
    {
        this.channels = channels?.ToArray() ?? throw new ArgumentNullException(nameof(channels));
        this.options = options ?? new ExternalDeliveryOptions();
        this.failureSink = failureSink;
        this.successSink = successSink;
    }

    public Task DeliverAsync(
        ExternalDeliveryWorkItem item,
        CancellationToken cancellationToken = default)
    {
        return options.DeliveryMode switch
        {
            ExternalDeliveryMode.Parallel => DeliverParallelAsync(item, cancellationToken),
            ExternalDeliveryMode.ParallelByRecipient => DeliverParallelByRecipientAsync(item, cancellationToken),
            _ => DeliverSequentialAsync(item, cancellationToken)
        };
    }

    private async Task DeliverSequentialAsync(
        ExternalDeliveryWorkItem item,
        CancellationToken cancellationToken)
    {
        foreach (RecipientSnapshot recipient in item.Recipients)
        {
            foreach (IExternalMessageChannel channel in channels)
            {
                if (!ShouldSend(channel, recipient, item.RequestedChannels))
                {
                    continue;
                }

                await DeliverOneAsync(channel, recipient, item, cancellationToken);
            }
        }
    }

    private async Task DeliverParallelAsync(
        ExternalDeliveryWorkItem item,
        CancellationToken cancellationToken)
    {
        int maxConcurrency = Math.Max(1, options.MaxConcurrency);
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

        await Parallel.ForEachAsync(
            work,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = maxConcurrency,
                CancellationToken = cancellationToken
            },
            async (entry, token) =>
            {
                await DeliverOneAsync(entry.Channel, entry.Recipient, item, token);
            });
    }

    private async Task DeliverParallelByRecipientAsync(
        ExternalDeliveryWorkItem item,
        CancellationToken cancellationToken)
    {
        int maxConcurrency = Math.Max(1, options.MaxConcurrency);
        await Parallel.ForEachAsync(
            item.Recipients,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = maxConcurrency,
                CancellationToken = cancellationToken
            },
            async (recipient, token) =>
            {
                foreach (IExternalMessageChannel channel in channels)
                {
                    if (!ShouldSend(channel, recipient, item.RequestedChannels))
                    {
                        continue;
                    }

                    await DeliverOneAsync(channel, recipient, item, token);
                }
            });
    }

    private async Task DeliverOneAsync(
        IExternalMessageChannel channel,
        RecipientSnapshot recipient,
        ExternalDeliveryWorkItem item,
        CancellationToken cancellationToken)
    {
        try
        {
            MessageRecipient resolved = new(
                recipient.UserId,
                recipient.UserName,
                recipient.Email,
                recipient.SlackUserId,
                recipient.Mobile,
                recipient.PreferredChannels);

            await channel.SendAsync(resolved, item.Request, item.NotificationId, cancellationToken);

            if (successSink is not null)
            {
                await successSink.RecordAsync(
                    new ExternalDeliverySuccess(
                        item.NotificationId,
                        recipient.UserId,
                        channel.Channel,
                        DateTimeOffset.UtcNow,
                        item.Request.CorrelationId),
                    cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (failureSink is null)
            {
                return;
            }

            await failureSink.RecordAsync(
                new ExternalDeliveryFailure(
                    item.NotificationId,
                    recipient.UserId,
                    channel.Channel,
                    exception.Message,
                    DateTimeOffset.UtcNow,
                    item.Request.CorrelationId),
                cancellationToken);
        }
    }

    private static bool ShouldSend(
        IExternalMessageChannel channel,
        RecipientSnapshot recipient,
        MessageChannel requestedChannels)
    {
        if (channel.Channel == MessageChannel.InApp || channel.Channel == MessageChannel.None)
        {
            return false;
        }

        MessageChannel effectiveRequested = requestedChannels == MessageChannel.None
            ? recipient.PreferredChannels
            : requestedChannels;

        return (effectiveRequested & channel.Channel) != 0
            && (recipient.PreferredChannels & channel.Channel) != 0;
    }
}
