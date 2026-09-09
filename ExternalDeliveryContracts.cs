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
    public int MaxAttempts { get; init; } = 5;
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(2);
    public string DurableQueuePath { get; init; } = "data/common-messaging";
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
    DateTimeOffset EnqueuedAt,
    int Attempt = 0);

public sealed record ExternalDeliveryFailure(
    Guid NotificationId,
    string RecipientUserId,
    MessageChannel Channel,
    string ErrorCode,
    DateTimeOffset OccurredAt,
    string? CorrelationId = null,
    int Attempt = 0);

public sealed record ExternalDeliverySuccess(
    Guid NotificationId,
    string RecipientUserId,
    MessageChannel Channel,
    DateTimeOffset DeliveredAt,
    string? CorrelationId = null);

public sealed record ExternalDeliveryAttemptResult(
    int Delivered,
    int Failed,
    string? LastErrorCode)
{
    public bool Succeeded => Failed == 0;
}

public sealed record ExternalDeliveryQueueHealth(
    bool IsAvailable,
    long Pending,
    long DeadLettered,
    string Message);

public sealed record ExternalDeliveryDeadLetter(
    ExternalDeliveryWorkItem Item,
    string ErrorCode,
    DateTimeOffset DeadLetteredAt);

public interface IExternalDeliveryQueue
{
    ValueTask EnqueueAsync(ExternalDeliveryWorkItem item, CancellationToken cancellationToken = default);
    IAsyncEnumerable<ExternalDeliveryWorkItem> ReadAllAsync(CancellationToken cancellationToken = default);

    ValueTask CompleteAsync(Guid notificationId, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    ValueTask RetryAsync(ExternalDeliveryWorkItem item, TimeSpan delay, CancellationToken cancellationToken = default)
        => EnqueueAsync(item with { Attempt = item.Attempt + 1 }, cancellationToken);

    ValueTask DeadLetterAsync(ExternalDeliveryWorkItem item, string errorCode, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}

public interface IExternalDeliveryDeadLetterStore
{
    Task<IReadOnlyList<ExternalDeliveryDeadLetter>> GetDeadLettersAsync(CancellationToken cancellationToken = default);
    ValueTask<bool> ReplayAsync(Guid notificationId, CancellationToken cancellationToken = default);
    ValueTask<int> ReplayAllAsync(CancellationToken cancellationToken = default);
}

public interface IExternalDeliveryQueueHealth
{
    Task<ExternalDeliveryQueueHealth> CheckHealthAsync(CancellationToken cancellationToken = default);
}

public interface IExternalDeliveryDispatcher
{
    Task DeliverAsync(ExternalDeliveryWorkItem item, CancellationToken cancellationToken = default);
}

public interface IRetryableExternalDeliveryDispatcher : IExternalDeliveryDispatcher
{
    Task<ExternalDeliveryAttemptResult> DeliverWithResultAsync(ExternalDeliveryWorkItem item, CancellationToken cancellationToken = default);
}

public interface IExternalDeliveryFailureSink
{
    Task RecordAsync(ExternalDeliveryFailure failure, CancellationToken cancellationToken = default);
}

public interface IExternalDeliverySuccessSink
{
    Task RecordAsync(ExternalDeliverySuccess success, CancellationToken cancellationToken = default);
}

public interface IExternalDeliveryIdempotencyStore
{
    Task<bool> HasDeliveredAsync(Guid notificationId, string recipientUserId, MessageChannel channel, CancellationToken cancellationToken = default);
    Task MarkDeliveredAsync(Guid notificationId, string recipientUserId, MessageChannel channel, CancellationToken cancellationToken = default);
}

public interface IChannelSecretResolver
{
    Task<string?> GetSecretAsync(string secretName, CancellationToken cancellationToken = default);
}

public interface IMessageSignalSender
{
    Task SignalAsync(
        Guid notificationId,
        IReadOnlyCollection<string> recipientUserIds,
        CancellationToken cancellationToken = default);
}
