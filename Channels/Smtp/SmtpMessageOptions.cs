namespace Common.Messaging.Channels.Smtp;

public sealed class SmtpMessageOptions
{
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; } = 587;
    public bool EnableSsl { get; init; } = true;
    public string FromAddress { get; init; } = string.Empty;
    public string UserName { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public string UserNameSecretName { get; init; } = "messaging/smtp/username";
    public string PasswordSecretName { get; init; } = "messaging/smtp/password";
}
