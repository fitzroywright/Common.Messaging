namespace Common.Messaging;

public sealed class MessageRequest
{
    public IReadOnlyCollection<string> RecipientIds { get; init; } = Array.Empty<string>();
    public string Title { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
    public MessageSeverity Severity { get; init; } = MessageSeverity.Information;
    public MessageChannel Channels { get; init; } = MessageChannel.InApp;
    public string? Source { get; init; }
    public string? CorrelationId { get; init; }
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

public sealed record MessageNotification(
    Guid Id,
    string RecipientUserId,
    string Title,
    string Body,
    MessageSeverity Severity,
    string? Source,
    string? CorrelationId,
    DateTimeOffset CreatedAt,
    bool IsDismissed = false);

public sealed record MessageSendResult(Guid NotificationId, bool Succeeded, string? Error = null);
