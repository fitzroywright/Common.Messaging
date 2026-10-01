namespace Common.Messaging.Channels.WhatsApp;

public sealed class WhatsAppMessageOptions
{
    public bool Enabled { get; init; }
    public string GraphApiVersion { get; init; } = "v23.0";
    public string PhoneNumberId { get; init; } = string.Empty;
    public string AccessToken { get; init; } = string.Empty;
    public string AccessTokenSecretName { get; init; } = "messaging/whatsapp/access-token";

    public string? ConfigurationError()
    {
        if (!Enabled) return "WhatsApp delivery is disabled.";
        if (string.IsNullOrWhiteSpace(GraphApiVersion)) return "WhatsApp GraphApiVersion is required.";
        if (string.IsNullOrWhiteSpace(PhoneNumberId)) return "WhatsApp PhoneNumberId is required.";
        if (string.IsNullOrWhiteSpace(AccessTokenSecretName) && string.IsNullOrWhiteSpace(AccessToken))
            return "WhatsApp AccessTokenSecretName or AccessToken is required.";
        return null;
    }
}
