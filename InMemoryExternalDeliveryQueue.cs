namespace Common.Messaging;

using System.Threading.Channels;

public sealed class InMemoryExternalDeliveryQueue : IExternalDeliveryQueue
{
    private readonly Channel<ExternalDeliveryWorkItem> channel;

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

    public ValueTask EnqueueAsync(
        ExternalDeliveryWorkItem item,
        CancellationToken cancellationToken = default)
    {
        return channel.Writer.WriteAsync(item, cancellationToken);
    }

    public IAsyncEnumerable<ExternalDeliveryWorkItem> ReadAllAsync(
        CancellationToken cancellationToken = default)
    {
        return channel.Reader.ReadAllAsync(cancellationToken);
    }
}
