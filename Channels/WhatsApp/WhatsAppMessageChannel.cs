namespace Common.Messaging.Channels.WhatsApp;

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

public sealed class WhatsAppMessageChannel : IExternalMessageChannel
{
    private readonly HttpClient httpClient;
    private readonly WhatsAppMessageOptions options;
    private readonly IChannelSecretResolver? secretResolver;

    public WhatsAppMessageChannel(
        HttpClient httpClient,
        WhatsAppMessageOptions options,
        IChannelSecretResolver? secretResolver = null)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.secretResolver = secretResolver;
    }

    public MessageChannel Channel => MessageChannel.WhatsApp;

    public async Task SendAsync(
        MessageRecipient recipient,
        MessageRequest request,
        Guid notificationId,
        CancellationToken cancellationToken = default)
    {
        string? configurationError = options.ConfigurationError();
        if (configurationError is not null)
            throw new InvalidOperationException(configurationError);

        if (string.IsNullOrWhiteSpace(recipient.Mobile))
            throw new InvalidOperationException("Recipient has no mobile number.");

        string? accessToken = await ResolveAsync(
            options.AccessTokenSecretName,
            options.AccessToken,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(accessToken))
            throw new InvalidOperationException("WhatsApp Cloud API access token is unavailable.");

        string endpoint =
            $"https://graph.facebook.com/{options.GraphApiVersion.Trim('/')}/{Uri.EscapeDataString(options.PhoneNumberId)}/messages";

        object payload = BuildPayload(recipient.Mobile, request);

        using HttpRequestMessage httpRequest = new(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json")
        };
        httpRequest.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", accessToken);

        using HttpResponseMessage response =
            await httpClient.SendAsync(httpRequest, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"Meta WhatsApp Cloud API returned {(int)response.StatusCode} ({response.ReasonPhrase}): {body}");
        }
    }

    private static object BuildPayload(string mobile, MessageRequest request)
    {
        if (TryGetTemplate(request, out string? templateName, out string language, out IReadOnlyList<string> parameters))
        {
            return new
            {
                messaging_product = "whatsapp",
                recipient_type = "individual",
                to = mobile,
                type = "template",
                template = new
                {
                    name = templateName,
                    language = new { code = language },
                    components = parameters.Count == 0
                        ? null
                        : new[]
                        {
                            new
                            {
                                type = "body",
                                parameters = parameters
                                    .Select(value => new { type = "text", text = value })
                                    .ToArray()
                            }
                        }
                }
            };
        }

        return new
        {
            messaging_product = "whatsapp",
            recipient_type = "individual",
            to = mobile,
            type = "text",
            text = new
            {
                preview_url = false,
                body = string.IsNullOrWhiteSpace(request.Body)
                    ? request.Title
                    : request.Body
            }
        };
    }

    private static bool TryGetTemplate(
        MessageRequest request,
        out string? name,
        out string language,
        out IReadOnlyList<string> parameters)
    {
        name = null;
        language = "en_US";
        parameters = [];

        if (request.Metadata is null ||
            !request.Metadata.TryGetValue("WhatsAppTemplateName", out string? configuredName) ||
            string.IsNullOrWhiteSpace(configuredName))
        {
            return false;
        }

        name = configuredName.Trim();
        if (request.Metadata.TryGetValue("WhatsAppTemplateLanguage", out string? configuredLanguage) &&
            !string.IsNullOrWhiteSpace(configuredLanguage))
        {
            language = configuredLanguage.Trim();
        }

        if (request.Metadata.TryGetValue("WhatsAppTemplateParameters", out string? rawParameters) &&
            !string.IsNullOrWhiteSpace(rawParameters))
        {
            parameters = rawParameters
                .Split('|', StringSplitOptions.TrimEntries)
                .ToArray();
        }

        return true;
    }

    private async Task<string?> ResolveAsync(
        string secretName,
        string fallback,
        CancellationToken cancellationToken)
    {
        if (secretResolver is not null && !string.IsNullOrWhiteSpace(secretName))
        {
            string? secret = await secretResolver.GetSecretAsync(
                secretName,
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(secret))
                return secret;
        }

        return fallback;
    }
}
