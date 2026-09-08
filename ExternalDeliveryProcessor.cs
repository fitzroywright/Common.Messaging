namespace Common.Messaging;

public sealed class ExternalDeliveryProcessor
{
    private readonly IExternalDeliveryQueue queue;
    private readonly IExternalDeliveryDispatcher dispatcher;
    private readonly IExternalDeliveryFailureSink? failureSink;

    public ExternalDeliveryProcessor(
        IExternalDeliveryQueue queue,
        IExternalDeliveryDispatcher dispatcher,
        IExternalDeliveryFailureSink? failureSink = null)
    {
        this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
        this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        this.failureSink = failureSink;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        await foreach (ExternalDeliveryWorkItem item in queue.ReadAllAsync(cancellationToken))
        {
            try
            {
                await dispatcher.DeliverAsync(item, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                if (failureSink is null)
                {
                    continue;
                }

                await failureSink.RecordAsync(
                    new ExternalDeliveryFailure(
                        item.NotificationId,
                        string.Empty,
                        MessageChannel.None,
                        exception.Message,
                        DateTimeOffset.UtcNow),
                    cancellationToken);
            }
        }
    }
}
