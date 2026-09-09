namespace Common.Messaging;

[Flags]
public enum MessageChannel
{
    None = 0,
    InApp = 1,
    Smtp = 2,
    MsTeams = 4,
    MsEmail = 8,
    Slack = 16,
    WhatsApp = 32,
    AudioAlert = 64,
    Sms = 128
}
