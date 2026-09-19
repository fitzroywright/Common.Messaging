using Xunit;

namespace Common.Messaging.UnitTests;

public sealed class MessagingDiagnosticsContractTests
{
    [Fact]
    public void Provider_acceptance_is_not_delivery()
    {
        MessageDeliveryState state = MessagingDeliverySemantics.ProviderSendCompleted(providerSupportsDeliveryReceipts: true);
        Assert.Equal(MessageDeliveryState.ProviderAccepted, state);
        Assert.NotEqual(MessageDeliveryState.Delivered, state);
    }

    [Fact]
    public void Delivery_receipt_can_confirm_delivery()
        => Assert.Equal(MessageDeliveryState.Delivered, MessagingDeliverySemantics.Receipt(delivered: true));

    [Fact]
    public void Negative_delivery_receipt_is_failure()
        => Assert.Equal(MessageDeliveryState.Failed, MessagingDeliverySemantics.Receipt(delivered: false));

    [Fact]
    public void Diagnostics_snapshot_keeps_dead_letter_separate_from_failures()
    {
        var snapshot = new MessagingDiagnosticsSnapshot(
            "App",
            "Instance",
            OutboundQueueDepth: 10,
            OldestOutboundAge: TimeSpan.FromMinutes(3),
            Dispatching: 1,
            Retrying: 2,
            Failures: 4,
            DeadLetter: 1,
            InboundActivity: 5,
            LastSuccessfulDeliveryAtUtc: null,
            ObservedAtUtc: DateTimeOffset.UtcNow,
            Providers: []);

        Assert.Equal(4, snapshot.Failures);
        Assert.Equal(1, snapshot.DeadLetter);
    }

    [Fact]
    public void Trace_preserves_message_and_correlation_identity()
    {
        Guid id = Guid.NewGuid();
        var trace = new MessageDeliveryTrace(
            id,
            "corr-1",
            "RequestPortal",
            "Email",
            2,
            MessageDeliveryState.ProviderAccepted,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            []);
        Assert.Equal(id, trace.MessageId);
        Assert.Equal("corr-1", trace.CorrelationId);
    }
}
