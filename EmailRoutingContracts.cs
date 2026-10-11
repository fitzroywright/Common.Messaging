namespace Common.Messaging;

// Discovery is separate from transport; applications own address registration and routing policy.
public interface IEmailRecipientDirectory
{
    Task<EmailGroupMembership> GetGroupAsync(string providerReference, CancellationToken cancellationToken = default);
}

public sealed record EmailGroupMembership(string ProviderReference, string Address, bool Supported,
    IReadOnlyList<string> Members, string? AttentionReason = null);

// A timeout or a lost send response must never be treated as proof that nothing was delivered.
public sealed class ExternalDeliveryOutcomeUnknownException : Exception
{
    public ExternalDeliveryOutcomeUnknownException(Exception inner) : base("External delivery outcome requires reconciliation.", inner) { }
}
