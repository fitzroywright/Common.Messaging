namespace Common.Messaging.Channels.MicrosoftGraph;

public sealed class MicrosoftGraphEmailOptions
{
    public string TenantId { get; init; } = string.Empty;
    public string ClientId { get; init; } = string.Empty;
    public string ClientSecret { get; init; } = string.Empty;
    public string ClientSecretName { get; init; } = "messaging/msgraph/client-secret";
    public string SenderUpn { get; init; } = string.Empty;
}
