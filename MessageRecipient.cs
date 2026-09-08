namespace Common.Messaging;

public sealed record MessageRecipient(
    string UserId,
    string UserName,
    string? Email = null,
    string? SlackUserId = null,
    string? Mobile = null,
    MessageChannel PreferredChannels = MessageChannel.InApp);

public interface IMessageRecipientDirectory
{
    Task<IReadOnlyList<MessageRecipient>> ResolveAsync(
        IReadOnlyCollection<string> recipientIds,
        CancellationToken cancellationToken = default);
}
