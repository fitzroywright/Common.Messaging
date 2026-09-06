namespace Common.Messaging;

public sealed class InMemoryMessageStore : IMessageStore
{
    private readonly object sync = new();
    private readonly List<MessageNotification> messages = new();

    public Task AddAsync(MessageNotification notification, CancellationToken cancellationToken = default)
    {
        lock (sync) messages.Add(notification);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MessageNotification>> GetActiveAsync(string recipientUserId, CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            IReadOnlyList<MessageNotification> result = messages
                .Where(x => x.RecipientUserId == recipientUserId && !x.IsDismissed)
                .OrderByDescending(x => x.CreatedAt)
                .ToArray();
            return Task.FromResult(result);
        }
    }

    public Task DismissAsync(Guid notificationId, string recipientUserId, CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            int index = messages.FindIndex(x => x.Id == notificationId && x.RecipientUserId == recipientUserId);
            if (index >= 0) messages[index] = messages[index] with { IsDismissed = true };
        }
        return Task.CompletedTask;
    }
}
