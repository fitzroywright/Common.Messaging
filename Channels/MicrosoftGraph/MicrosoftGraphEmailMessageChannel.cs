namespace Common.Messaging.Channels.MicrosoftGraph;

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

public sealed class MicrosoftGraphEmailMessageChannel : IExternalMessageChannel, IExternalMessageChannelDiagnostic
{
    private readonly HttpClient httpClient;
    private readonly MicrosoftGraphEmailOptions options;
    private readonly IChannelSecretResolver? secretResolver;

    public MicrosoftGraphEmailMessageChannel(
        HttpClient httpClient,
        MicrosoftGraphEmailOptions options,
        IChannelSecretResolver? secretResolver = null)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.secretResolver = secretResolver;
    }

    public MessageChannel Channel => MessageChannel.MsEmail;

    public async Task<MessagingProviderHealth> VerifyAsync(CancellationToken cancellationToken = default)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        bool configured =
            !string.IsNullOrWhiteSpace(options.TenantId) &&
            !string.IsNullOrWhiteSpace(options.ClientId) &&
            !string.IsNullOrWhiteSpace(options.SenderUpn);

        if (!configured)
        {
            return new MessagingProviderHealth(
                "Microsoft Graph Email",
                false,
                false,
                MessageDeliveryState.Unknown,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow - started,
                "TenantId, ClientId or SenderUpn is missing.");
        }

        string clientSecret = await ResolveSecretAsync(
            options.ClientSecretName,
            options.ClientSecret,
            cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(clientSecret))
        {
            return new MessagingProviderHealth(
                "Microsoft Graph Email",
                false,
                false,
                MessageDeliveryState.Unknown,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow - started,
                "Client secret is unavailable.");
        }

        try
        {
            string accessToken = await AcquireAppTokenAsync(clientSecret, cancellationToken).ConfigureAwait(false);
            using HttpRequestMessage request = new(
                HttpMethod.Get,
                $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(options.SenderUpn.Trim())}/mailFolders/inbox?$select=id,displayName,totalItemCount,unreadItemCount");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new MessagingProviderHealth(
                    "Microsoft Graph Email",
                    true,
                    false,
                    MessageDeliveryState.Failed,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow - started,
                    $"Mailbox probe returned HTTP {(int)response.StatusCode}.");
            }

            return new MessagingProviderHealth(
                "Microsoft Graph Email",
                true,
                true,
                MessageDeliveryState.ProviderAccepted,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow - started,
                $"Credentials are valid and sender mailbox '{options.SenderUpn.Trim()}' is accessible.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new MessagingProviderHealth(
                "Microsoft Graph Email",
                true,
                false,
                MessageDeliveryState.Failed,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow - started,
                exception is HttpRequestException ? "Microsoft Graph authentication or mailbox verification failed." : exception.GetType().Name);
        }
    }

    public async Task SendAsync(
        MessageRecipient recipient,
        MessageRequest request,
        Guid notificationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(recipient.Email))
            throw new InvalidOperationException("Recipient has no email address.");
        if (string.IsNullOrWhiteSpace(options.TenantId))
            throw new InvalidOperationException("Microsoft Graph TenantId is required.");
        if (string.IsNullOrWhiteSpace(options.ClientId))
            throw new InvalidOperationException("Microsoft Graph ClientId is required.");
        if (string.IsNullOrWhiteSpace(options.SenderUpn))
            throw new InvalidOperationException("Microsoft Graph email SenderUpn is required.");

        if (options.SkipSenderRecipient && string.Equals(options.SenderUpn.Trim(), recipient.Email.Trim(), StringComparison.OrdinalIgnoreCase))
            return;

        // Use the small-attachment sendMail API. Larger reports must not be silently truncated.
        if (request.Attachments.Sum(attachment => (long)attachment.Content.Length) > 2_500_000)
            throw new InvalidOperationException("Microsoft Graph email attachments exceed the supported 2.5 MB total. Split the report before retrying.");

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            message = new
            {
                subject = string.IsNullOrWhiteSpace(request.Title) ? "(no subject)" : request.Title,
                body = new { contentType = request.BodyIsHtml ? "HTML" : "Text", content = request.Body ?? string.Empty },
                toRecipients = new[]
                {
                    new { emailAddress = new { address = recipient.Email.Trim() } }
                },
                attachments = request.Attachments.Select(attachment => new Dictionary<string, object>
                {
                    ["@odata.type"] = "#microsoft.graph.fileAttachment",
                    ["name"] = attachment.FileName,
                    ["contentType"] = attachment.ContentType,
                    ["contentBytes"] = Convert.ToBase64String(attachment.Content)
                }).ToArray()
            },
            saveToSentItems = true
        });
        if (payload.Length > 4_000_000)
            throw new InvalidOperationException("Microsoft Graph email payload exceeds the supported 4 MB total. Split the report before retrying.");

        string clientSecret = await ResolveSecretAsync(
            options.ClientSecretName,
            options.ClientSecret,
            cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(clientSecret))
            throw new InvalidOperationException("Microsoft Graph client secret is unavailable.");

        string accessToken = await AcquireAppTokenAsync(clientSecret, cancellationToken).ConfigureAwait(false);
        using HttpRequestMessage message = new(
            HttpMethod.Post,
            $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(options.SenderUpn.Trim())}/sendMail");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        message.Content = new ByteArrayContent(payload);
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using HttpResponseMessage response = await httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException($"Microsoft Graph email send failed with HTTP {(int)response.StatusCode}: {Sanitize(detail)}");
        }
    }

    private async Task<string> AcquireAppTokenAsync(string clientSecret, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(
            HttpMethod.Post,
            $"https://login.microsoftonline.com/{Uri.EscapeDataString(options.TenantId.Trim())}/oauth2/v2.0/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = options.ClientId.Trim(),
                ["client_secret"] = clientSecret,
                ["scope"] = "https://graph.microsoft.com/.default",
                ["grant_type"] = "client_credentials"
            })
        };

        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Microsoft Graph token acquisition failed with HTTP {(int)response.StatusCode}: {Sanitize(json)}");

        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("access_token", out JsonElement tokenElement) ||
            string.IsNullOrWhiteSpace(tokenElement.GetString()))
            throw new InvalidOperationException("Microsoft Graph token response did not contain an access token.");

        return tokenElement.GetString()!;
    }

    private async Task<string> ResolveSecretAsync(string name, string fallback, CancellationToken cancellationToken)
    {
        if (secretResolver is not null && !string.IsNullOrWhiteSpace(name))
        {
            string? value = await secretResolver.GetSecretAsync(name, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return fallback;
    }

    private static string Sanitize(string value)
        => string.IsNullOrWhiteSpace(value) ? "No response body." : value.Length <= 512 ? value : value[..512];
}
