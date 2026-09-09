namespace Common.Messaging.Channels.Teams;

public sealed class TeamsMessageOptions
{
    public string WebhookUrl { get; init; } = string.Empty;
    public string WebhookSecretName { get; init; } = "messaging/teams/webhook-url";
}
