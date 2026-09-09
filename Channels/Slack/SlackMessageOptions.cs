namespace Common.Messaging.Channels.Slack;

public sealed class SlackMessageOptions
{
    public string BotToken { get; init; } = string.Empty;
    public string BotTokenSecretName { get; init; } = "messaging/slack/bot-token";
}
