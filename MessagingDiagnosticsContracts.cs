namespace Common.Messaging;

public enum MessageDeliveryState
{
    Queued,
    Dispatching,
    ProviderAccepted,
    Delivered,
    Failed,
    DeadLetter,
    Unknown
}

public sealed record MessagingProviderHealth(
    string Provider,
    bool Configured,
    bool Reachable,
    MessageDeliveryState State,
    DateTimeOffset ObservedAtUtc,
    TimeSpan? Latency = null,
    string? Reason = null);

public sealed record MessagingDiagnosticsSnapshot(
    string ApplicationId,
    string InstanceId,
    long OutboundQueueDepth,
    TimeSpan? OldestOutboundAge,
    long Dispatching,
    long Retrying,
    long Failures,
    long DeadLetter,
    long InboundActivity,
    DateTimeOffset? LastSuccessfulDeliveryAtUtc,
    DateTimeOffset ObservedAtUtc,
    IReadOnlyList<MessagingProviderHealth> Providers);

public sealed record MessageDeliveryTraceStage(
    MessageDeliveryState State,
    DateTimeOffset AtUtc,
    string Provider,
    int Attempt,
    string? Detail = null);

public sealed record MessageDeliveryTrace(
    Guid MessageId,
    string? CorrelationId,
    string OriginatingApplication,
    string Provider,
    int Attempt,
    MessageDeliveryState State,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ObservedAtUtc,
    IReadOnlyList<MessageDeliveryTraceStage> Stages,
    string? FailureCode = null);

public static class MessagingDeliverySemantics
{
    public static MessageDeliveryState ProviderSendCompleted(bool providerSupportsDeliveryReceipts) =>
        providerSupportsDeliveryReceipts
            ? MessageDeliveryState.ProviderAccepted
            : MessageDeliveryState.ProviderAccepted;

    public static MessageDeliveryState Receipt(bool delivered) =>
        delivered ? MessageDeliveryState.Delivered : MessageDeliveryState.Failed;
}
