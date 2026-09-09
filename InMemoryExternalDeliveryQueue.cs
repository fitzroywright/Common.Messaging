namespace Common.Messaging;

using System.Threading.Channels;

public sealed class InMemoryExternalDeliveryQueue : IExternalDeliveryQueue, IExternalDeliveryQueueHealth
{
    private readonly Channel<ExternalDeliveryWorkItem> channel;
    private long pending;

    public InMemoryExternalDeliveryQueue(ExternalDeliveryOptions? options = null)
    {
        int capacity = Math.Max(1, options?.QueueCapacity ?? 256);
        channel = Channel.CreateBounded<ExternalDeliveryWorkItem>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });
    }

    public async ValueTask EnqueueAsync(
        ExternalDeliveryWorkItem item,
        CancellationToken cancellationToken = default)
    {
        await channel.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref pending);
    }

    public async IAsyncEnumerable<ExternalDeliveryWorkItem> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (ExternalDeliveryWorkItem item in channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return item;
        }
    }

    public ValueTask CompleteAsync(Guid notificationId, CancellationToken cancellationToken = default)
    {
        Interlocked.Decrement(ref pending);
        return ValueTask.CompletedTask;
    }

    public async ValueTask RetryAsync(
        ExternalDeliveryWorkItem item,
        TimeSpan delay,
        CancellationToken cancellationToken = default)
    {
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        await channel.Writer.WriteAsync(item with { Attempt = item.Attempt + 1 }, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DeadLetterAsync(
        ExternalDeliveryWorkItem item,
        string errorCode,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Decrement(ref pending);
        return ValueTask.CompletedTask;
    }

    public Task<ExternalDeliveryQueueHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new ExternalDeliveryQueueHealth(true, Math.Max(0, Interlocked.Read(ref pending)), 0, "In-memory queue is available but not durable."));
    }
}
