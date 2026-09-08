namespace Common.Messaging;

public interface IMessageStore
{
    Task AddAsync(MessageNotification notification, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MessageNotification>> GetActiveAsync(string recipientUserId, CancellationToken cancellationToken = default);
    Task DismissAsync(Guid notificationId, string recipientUserId, CancellationToken cancellationToken = default);
}

public interface IExternalMessageChannel
{
    MessageChannel Channel { get; }
    Task SendAsync(MessageRecipient recipient, MessageRequest request, Guid notificationId, CancellationToken cancellationToken = default);
}

public interface IMessageService
{
    Task<MessageSendResult> SendAsync(MessageRequest request, CancellationToken cancellationToken = default);
}
