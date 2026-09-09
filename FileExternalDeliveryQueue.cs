using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Common.Messaging;

public sealed class FileExternalDeliveryQueue : IExternalDeliveryQueue, IExternalDeliveryQueueHealth, IExternalDeliveryDeadLetterStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ExternalDeliveryOptions options;
    private readonly string rootPath;
    private readonly string pendingPath;
    private readonly string leasedPath;
    private readonly string deadPath;

    public FileExternalDeliveryQueue(ExternalDeliveryOptions? options = null)
    {
        this.options = options ?? new ExternalDeliveryOptions();
        rootPath = Path.GetFullPath(this.options.DurableQueuePath);
        pendingPath = Path.Combine(rootPath, "pending");
        leasedPath = Path.Combine(rootPath, "leased");
        deadPath = Path.Combine(rootPath, "dead-letter");
        EnsureDirectories();
    }

    public async ValueTask EnqueueAsync(
        ExternalDeliveryWorkItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        QueueEnvelope envelope = new(item, DateTimeOffset.UtcNow, null);
        string path = PendingFile(item.NotificationId);
        await WriteAtomicallyAsync(path, envelope, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<ExternalDeliveryWorkItem> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            RecoverExpiredLeases();
            bool yielded = false;

            foreach (string file in Directory.EnumerateFiles(pendingPath, "*.json").OrderBy(path => path, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                QueueEnvelope? envelope = await ReadEnvelopeAsync(file, cancellationToken).ConfigureAwait(false);
                if (envelope is null || envelope.NextAttemptAt > DateTimeOffset.UtcNow)
                {
                    continue;
                }

                string leasedFile = LeasedFile(envelope.Item.NotificationId);
                try
                {
                    File.Move(file, leasedFile, false);
                }
                catch (IOException)
                {
                    continue;
                }

                File.SetLastWriteTimeUtc(leasedFile, DateTime.UtcNow);
                yielded = true;
                yield return envelope.Item;
            }

            if (!yielded)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public ValueTask CompleteAsync(Guid notificationId, CancellationToken cancellationToken = default)
    {
        DeleteIfExists(LeasedFile(notificationId));
        DeleteIfExists(PendingFile(notificationId));
        return ValueTask.CompletedTask;
    }

    public async ValueTask RetryAsync(
        ExternalDeliveryWorkItem item,
        TimeSpan delay,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ExternalDeliveryWorkItem retry = item with { Attempt = item.Attempt + 1 };
        QueueEnvelope envelope = new(retry, DateTimeOffset.UtcNow.Add(delay), null);
        await WriteAtomicallyAsync(PendingFile(item.NotificationId), envelope, cancellationToken).ConfigureAwait(false);
        DeleteIfExists(LeasedFile(item.NotificationId));
    }

    public async ValueTask DeadLetterAsync(
        ExternalDeliveryWorkItem item,
        string errorCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        QueueEnvelope envelope = new(item, DateTimeOffset.MaxValue, errorCode);
        await WriteAtomicallyAsync(DeadFile(item.NotificationId), envelope, cancellationToken).ConfigureAwait(false);
        DeleteIfExists(LeasedFile(item.NotificationId));
        DeleteIfExists(PendingFile(item.NotificationId));
    }

    public async Task<IReadOnlyList<ExternalDeliveryDeadLetter>> GetDeadLettersAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureDirectories();
        List<ExternalDeliveryDeadLetter> deadLetters = [];
        foreach (string file in Directory.EnumerateFiles(deadPath, "*.json").OrderBy(path => path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            QueueEnvelope? envelope = await ReadEnvelopeAsync(file, cancellationToken).ConfigureAwait(false);
            if (envelope is null) continue;
            string errorCode = string.IsNullOrWhiteSpace(envelope.ErrorCode) ? "DELIVERY_FAILED" : envelope.ErrorCode;
            DateTimeOffset deadLetteredAt = new(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
            deadLetters.Add(new ExternalDeliveryDeadLetter(envelope.Item, errorCode, deadLetteredAt));
        }
        return deadLetters;
    }

    public async ValueTask<bool> ReplayAsync(
        Guid notificationId,
        CancellationToken cancellationToken = default)
    {
        string deadFile = DeadFile(notificationId);
        QueueEnvelope? envelope = await ReadEnvelopeAsync(deadFile, cancellationToken).ConfigureAwait(false);
        if (envelope is null) return false;

        ExternalDeliveryWorkItem replay = envelope.Item with
        {
            Attempt = 0,
            EnqueuedAt = DateTimeOffset.UtcNow
        };
        await WriteAtomicallyAsync(
            PendingFile(notificationId),
            new QueueEnvelope(replay, DateTimeOffset.UtcNow, null),
            cancellationToken).ConfigureAwait(false);
        DeleteIfExists(deadFile);
        return true;
    }

    public async ValueTask<int> ReplayAllAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ExternalDeliveryDeadLetter> deadLetters = await GetDeadLettersAsync(cancellationToken).ConfigureAwait(false);
        int replayed = 0;
        foreach (ExternalDeliveryDeadLetter deadLetter in deadLetters)
        {
            if (await ReplayAsync(deadLetter.Item.NotificationId, cancellationToken).ConfigureAwait(false))
            {
                replayed++;
            }
        }
        return replayed;
    }

    public Task<ExternalDeliveryQueueHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            EnsureDirectories();
            long pending = Directory.EnumerateFiles(pendingPath, "*.json").LongCount()
                + Directory.EnumerateFiles(leasedPath, "*.json").LongCount();
            long dead = Directory.EnumerateFiles(deadPath, "*.json").LongCount();
            return Task.FromResult(new ExternalDeliveryQueueHealth(true, pending, dead, "Durable file queue is available."));
        }
        catch
        {
            return Task.FromResult(new ExternalDeliveryQueueHealth(false, 0, 0, "Durable file queue is unavailable."));
        }
    }

    private void RecoverExpiredLeases()
    {
        DateTime staleBefore = DateTime.UtcNow.Subtract(options.LeaseDuration);
        foreach (string file in Directory.EnumerateFiles(leasedPath, "*.json"))
        {
            if (File.GetLastWriteTimeUtc(file) >= staleBefore)
            {
                continue;
            }

            string destination = Path.Combine(pendingPath, Path.GetFileName(file));
            try
            {
                File.Move(file, destination, false);
            }
            catch (IOException)
            {
                // Another worker recovered or completed it.
            }
        }
    }

    private async Task<QueueEnvelope?> ReadEnvelopeAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        try
        {
            await using FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return await JsonSerializer.DeserializeAsync<QueueEnvelope>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task WriteAtomicallyAsync(
        string path,
        QueueEnvelope envelope,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await using (FileStream stream = File.Open(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, envelope, JsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temp, path, true);
    }

    private void EnsureDirectories()
    {
        Directory.CreateDirectory(pendingPath);
        Directory.CreateDirectory(leasedPath);
        Directory.CreateDirectory(deadPath);
    }

    private string PendingFile(Guid id) => Path.Combine(pendingPath, $"{id:N}.json");
    private string LeasedFile(Guid id) => Path.Combine(leasedPath, $"{id:N}.json");
    private string DeadFile(Guid id) => Path.Combine(deadPath, $"{id:N}.json");

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private sealed record QueueEnvelope(
        ExternalDeliveryWorkItem Item,
        DateTimeOffset NextAttemptAt,
        string? ErrorCode);
}
