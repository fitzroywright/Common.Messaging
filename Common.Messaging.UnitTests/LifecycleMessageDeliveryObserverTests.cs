using Common.Diagnostics;
using Xunit;

namespace Common.Messaging.UnitTests;

public sealed class LifecycleMessageDeliveryObserverTests
{
    [Fact]
    public async Task ObserveAsync_EmitsDeliveryStateWithoutMessageContent()
    {
        var sink = new RecordingSink();
        var observer = new LifecycleMessageDeliveryObserver("RequestPortal", "portal-01", sink);
        Guid messageId = Guid.NewGuid();

        await observer.ObserveAsync(new MessageDeliveryTrace(
            messageId,
            "corr-1",
            "RequestPortal",
            "Slack",
            2,
            MessageDeliveryState.Delivered,
            DateTimeOffset.UtcNow.AddSeconds(-1),
            DateTimeOffset.UtcNow,
            [],
            null));

        LifecycleEvent item = Assert.Single(sink.Items);
        Assert.Equal("MessagingDelivery", item.Flow);
        Assert.Equal("Delivered", item.Stage);
        Assert.Equal(messageId.ToString("D"), item.OperationId);
        Assert.Equal("Slack", item.Properties!["Provider"]);
    }

    private sealed class RecordingSink : ILifecycleEventSink
    {
        public List<LifecycleEvent> Items { get; } = [];
        public Task EmitAsync(LifecycleEvent lifecycleEvent, CancellationToken cancellationToken = default)
        {
            Items.Add(lifecycleEvent);
            return Task.CompletedTask;
        }
    }
}
