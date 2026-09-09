namespace Common.Messaging.Channels.Teams;

using System.Text;
using System.Text.Json;

public sealed class TeamsMessageChannel : IExternalMessageChannel
{
    private readonly HttpClient httpClient;
    private readonly TeamsMessageOptions options;
    private readonly IChannelSecretResolver? secretResolver;

    public TeamsMessageChannel(HttpClient httpClient, TeamsMessageOptions options, IChannelSecretResolver? secretResolver = null)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.secretResolver = secretResolver;
    }

    public MessageChannel Channel => MessageChannel.MsTeams;

    public async Task SendAsync(MessageRecipient recipient, MessageRequest request, Guid notificationId, CancellationToken cancellationToken = default)
    {
        string webhook = await ResolveWebhookAsync(cancellationToken).ConfigureAwait(false);
        using HttpRequestMessage httpRequest = new(HttpMethod.Post, webhook)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { title = request.Title, text = request.Body }),
                Encoding.UTF8,
                "application/json")
        };
        using HttpResponseMessage response = await httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private async Task<string> ResolveWebhookAsync(CancellationToken cancellationToken)
    {
        if (secretResolver is not null && !string.IsNullOrWhiteSpace(options.WebhookSecretName))
        {
            string? secret = await secretResolver.GetSecretAsync(options.WebhookSecretName, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(secret)) return secret;
        }

        if (!string.IsNullOrWhiteSpace(options.WebhookUrl)) return options.WebhookUrl;
        throw new InvalidOperationException("Teams webhook credential is unavailable.");
    }
}
