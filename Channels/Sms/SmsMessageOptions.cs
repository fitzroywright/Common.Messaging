namespace Common.Messaging.Channels.Sms;

public sealed class SmsMessageOptions
{
    public string Endpoint { get; init; } = string.Empty;
    public string EndpointSecretName { get; init; } = "messaging/sms/endpoint";
    public string ApiToken { get; init; } = string.Empty;
    public string ApiTokenSecretName { get; init; } = "messaging/sms/api-token";
}
