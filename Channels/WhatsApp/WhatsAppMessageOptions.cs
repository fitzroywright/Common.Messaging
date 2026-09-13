namespace Common.Messaging.Channels.WhatsApp;

public sealed class WhatsAppMessageOptions
{
    public string Endpoint { get; init; } = string.Empty;
    public string EndpointSecretName { get; init; } = "messaging/whatsapp/endpoint";
    public string ApiToken { get; init; } = string.Empty;
    public string ApiTokenSecretName { get; init; } = "messaging/whatsapp/api-token";
}
