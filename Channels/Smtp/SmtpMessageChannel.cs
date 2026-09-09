namespace Common.Messaging.Channels.Smtp;

using System.Net;
using System.Net.Mail;

public sealed class SmtpMessageChannel : IExternalMessageChannel
{
    private readonly SmtpMessageOptions options;
    private readonly IChannelSecretResolver? secretResolver;

    public SmtpMessageChannel(SmtpMessageOptions options, IChannelSecretResolver? secretResolver = null)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.secretResolver = secretResolver;
    }

    public MessageChannel Channel => MessageChannel.Smtp;

    public async Task SendAsync(MessageRecipient recipient, MessageRequest request, Guid notificationId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(recipient.Email))
        {
            throw new InvalidOperationException("Recipient has no email address.");
        }

        string userName = await ResolveAsync(options.UserNameSecretName, options.UserName, cancellationToken).ConfigureAwait(false);
        string password = await ResolveAsync(options.PasswordSecretName, options.Password, cancellationToken).ConfigureAwait(false);

        using SmtpClient client = new(options.Host, options.Port)
        {
            EnableSsl = options.EnableSsl,
            Credentials = string.IsNullOrWhiteSpace(userName)
                ? CredentialCache.DefaultNetworkCredentials
                : new NetworkCredential(userName, password)
        };
        using MailMessage message = new(options.FromAddress, recipient.Email)
        {
            Subject = request.Title ?? string.Empty,
            Body = request.Body ?? string.Empty
        };

        using CancellationTokenRegistration registration = cancellationToken.Register(client.SendAsyncCancel);
        await client.SendMailAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> ResolveAsync(string secretName, string fallback, CancellationToken cancellationToken)
    {
        if (secretResolver is not null && !string.IsNullOrWhiteSpace(secretName))
        {
            string? secret = await secretResolver.GetSecretAsync(secretName, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(secret)) return secret;
        }

        return fallback;
    }
}
