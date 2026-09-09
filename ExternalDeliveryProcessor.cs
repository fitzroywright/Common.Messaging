namespace Common.Messaging;

public sealed class ExternalDeliveryProcessor
{
    private readonly IExternalDeliveryQueue queue;
    private readonly IExternalDeliveryDispatcher dispatcher;
    private readonly ExternalDeliveryOptions options;
    private readonly IExternalDeliveryFailureSink? failureSink;

    public ExternalDeliveryProcessor(
        IExternalDeliveryQueue queue,
        IExternalDeliveryDispatcher dispatcher,
        ExternalDeliveryOptions? options = null,
        IExternalDeliveryFailureSink? failureSink = null)
    {
        this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        this.options = options ?? new ExternalDeliveryOptions();
        this.failureSink = failureSink;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        await foreach (ExternalDeliveryWorkItem item in queue.ReadAllAsync(cancellationToken))
        {
            try
            {
                ExternalDeliveryAttemptResult result = dispatcher is IRetryableExternalDeliveryDispatcher retryable
                    ? await retryable.DeliverWithResultAsync(item, cancellationToken).ConfigureAwait(false)
                    : await DeliverLegacyAsync(item, cancellationToken).ConfigureAwait(false);

                if (result.Succeeded)
                {
                    await queue.CompleteAsync(item.NotificationId, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                int nextAttempt = item.Attempt + 1;
                string errorCode = result.LastErrorCode ?? "DELIVERY_FAILED";
                if (nextAttempt >= Math.Max(1, options.MaxAttempts))
                {
                    await queue.DeadLetterAsync(item with { Attempt = nextAttempt }, errorCode, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await queue.RetryAsync(item, CalculateRetryDelay(nextAttempt), cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                string errorCode = ExternalDeliveryErrorSanitizer.GetErrorCode(exception);
                int nextAttempt = item.Attempt + 1;

                if (failureSink is not null)
                {
                    await failureSink.RecordAsync(
                        new ExternalDeliveryFailure(
                            item.NotificationId,
                            string.Empty,
                            MessageChannel.None,
                            errorCode,
                            DateTimeOffset.UtcNow,
                            item.Request.CorrelationId,
                            nextAttempt),
                        cancellationToken).ConfigureAwait(false);
                }

                if (nextAttempt >= Math.Max(1, options.MaxAttempts))
                {
                    await queue.DeadLetterAsync(item with { Attempt = nextAttempt }, errorCode, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await queue.RetryAsync(item, CalculateRetryDelay(nextAttempt), cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task<ExternalDeliveryAttemptResult> DeliverLegacyAsync(
        ExternalDeliveryWorkItem item,
        CancellationToken cancellationToken)
    {
        await dispatcher.DeliverAsync(item, cancellationToken).ConfigureAwait(false);
        return new ExternalDeliveryAttemptResult(1, 0, null);
    }

    private TimeSpan CalculateRetryDelay(int attempt)
    {
        double multiplier = Math.Pow(2, Math.Max(0, attempt - 1));
        double milliseconds = options.InitialRetryDelay.TotalMilliseconds * multiplier;
        return TimeSpan.FromMilliseconds(Math.Min(milliseconds, options.MaxRetryDelay.TotalMilliseconds));
    }
}
