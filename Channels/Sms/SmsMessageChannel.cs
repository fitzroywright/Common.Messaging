namespace Common.Messaging.Channels.Sms;

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

public sealed class SmsMessageChannel : IExternalMessageChannel
{
    private readonly HttpClient httpClient;
    private readonly SmsMessageOptions options;
    private readonly IChannelSecretResolver? secretResolver;

    public SmsMessageChannel(HttpClient httpClient, SmsMessageOptions options, IChannelSecretResolver? secretResolver = null)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.secretResolver = secretResolver;
    }

    public MessageChannel Channel => MessageChannel.Sms;

    public async Task SendAsync(MessageRecipient recipient, MessageRequest request, Guid notificationId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(recipient.Mobile))
        {
            throw new InvalidOperationException("Recipient has no mobile number.");
        }

        string endpoint = await ResolveAsync(options.EndpointSecretName, options.Endpoint, cancellationToken).ConfigureAwait(false);
        string token = await ResolveAsync(options.ApiTokenSecretName, options.ApiToken, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new InvalidOperationException("SMS endpoint is unavailable.");
        }

        using HttpRequestMessage httpRequest = new(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    to = recipient.Mobile,
                    title = request.Title,
                    body = request.Body,
                    notificationId
                }),
                Encoding.UTF8,
                "application/json")
        };
        if (!string.IsNullOrWhiteSpace(token))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using HttpResponseMessage response = await httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
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
