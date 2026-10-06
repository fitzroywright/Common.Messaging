namespace Common.Messaging.Channels.MicrosoftGraph;

public sealed class MicrosoftGraphEmailOptions
{
    public string TenantId { get; init; } = string.Empty;
    public string ClientId { get; init; } = string.Empty;
    public string ClientSecret { get; init; } = string.Empty;
    public string ClientSecretName { get; init; } = "messaging/msgraph/client-secret";
    public string SenderUpn { get; init; } = string.Empty;
    public bool SkipSenderRecipient { get; init; } = true;

    // Optional inbound mailbox support. The same Graph identity and mailbox are
    // deliberately shared with outbound email so applications have one
    // Common.Messaging configuration surface.
    public bool InboundEnabled { get; init; }
    public int InboundPollSeconds { get; init; } = 30;
    public int InboundPageSize { get; init; } = 50;
}
