namespace Common.Messaging;

public enum ExternalDeliveryMode
{
    Sequential = 0,
    Parallel = 1,
    ParallelByRecipient = 2
}

public sealed class ExternalDeliveryOptions
{
    public ExternalDeliveryMode DeliveryMode { get; init; } = ExternalDeliveryMode.Sequential;
    public int MaxConcurrency { get; init; } = 4;
    public int QueueCapacity { get; init; } = 256;
}

public sealed record RecipientSnapshot(
    string UserId,
    string UserName,
    string? Email,
    string? SlackUserId,
    string? Mobile,
    MessageChannel PreferredChannels);

public sealed record ExternalDeliveryWorkItem(
    Guid NotificationId,
    IReadOnlyList<RecipientSnapshot> Recipients,
    MessageChannel RequestedChannels,
    MessageRequest Request,
    string? ContextId,
    string? ContextName,
    DateTimeOffset EnqueuedAt);

public sealed record ExternalDeliveryFailure(
    Guid NotificationId,
    string RecipientUserId,
    MessageChannel Channel,
    string Error,
    DateTimeOffset OccurredAt);

public interface IExternalDeliveryQueue
{
    ValueTask EnqueueAsync(ExternalDeliveryWorkItem item, CancellationToken cancellationToken = default);
    IAsyncEnumerable<ExternalDeliveryWorkItem> ReadAllAsync(CancellationToken cancellationToken = default);
}

public interface IExternalDeliveryDispatcher
{
    Task DeliverAsync(ExternalDeliveryWorkItem item, CancellationToken cancellationToken = default);
}

public interface IExternalDeliveryFailureSink
{
    Task RecordAsync(ExternalDeliveryFailure failure, CancellationToken cancellationToken = default);
}

public interface IMessageSignalSender
{
    Task SignalAsync(
        Guid notificationId,
        IReadOnlyCollection<string> recipientUserIds,
        CancellationToken cancellationToken = default);
}
