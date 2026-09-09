namespace Common.Messaging;

public sealed class FileExternalDeliveryIdempotencyStore : IExternalDeliveryIdempotencyStore
{
    private readonly string rootPath;

    public FileExternalDeliveryIdempotencyStore(ExternalDeliveryOptions? options = null)
    {
        ExternalDeliveryOptions resolved = options ?? new ExternalDeliveryOptions();
        rootPath = Path.Combine(Path.GetFullPath(resolved.DurableQueuePath), "delivered");
        Directory.CreateDirectory(rootPath);
    }

    public Task<bool> HasDeliveredAsync(
        Guid notificationId,
        string recipientUserId,
        MessageChannel channel,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(File.Exists(GetPath(notificationId, recipientUserId, channel)));
    }

    public async Task MarkDeliveredAsync(
        Guid notificationId,
        string recipientUserId,
        MessageChannel channel,
        CancellationToken cancellationToken = default)
    {
        string path = GetPath(notificationId, recipientUserId, channel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, DateTimeOffset.UtcNow.ToString("O"), cancellationToken).ConfigureAwait(false);
    }

    private string GetPath(Guid notificationId, string recipientUserId, MessageChannel channel)
    {
        string safeRecipient = string.Concat(recipientUserId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        return Path.Combine(rootPath, $"{notificationId:N}_{safeRecipient}_{(int)channel}.done");
    }
}
