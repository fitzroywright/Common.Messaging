using System.Collections.Concurrent;

namespace Common.Messaging;

public sealed class InMemoryExternalDeliveryIdempotencyStore : IExternalDeliveryIdempotencyStore
{
    private readonly ConcurrentDictionary<string, byte> delivered = new(StringComparer.Ordinal);

    public Task<bool> HasDeliveredAsync(
        Guid notificationId,
        string recipientUserId,
        MessageChannel channel,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(delivered.ContainsKey(CreateKey(notificationId, recipientUserId, channel)));
    }

    public Task MarkDeliveredAsync(
        Guid notificationId,
        string recipientUserId,
        MessageChannel channel,
        CancellationToken cancellationToken = default)
    {
        delivered.TryAdd(CreateKey(notificationId, recipientUserId, channel), 0);
        return Task.CompletedTask;
    }

    private static string CreateKey(Guid notificationId, string recipientUserId, MessageChannel channel)
    {
        return $"{notificationId:N}|{recipientUserId}|{(int)channel}";
    }
}
