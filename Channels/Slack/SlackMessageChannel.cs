namespace Common.Messaging.Channels.Slack;

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

public sealed class SlackMessageChannel : IExternalMessageChannel
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient httpClient;
    private readonly SlackMessageOptions options;
    private readonly IChannelSecretResolver? secretResolver;

    public SlackMessageChannel(HttpClient httpClient, SlackMessageOptions options, IChannelSecretResolver? secretResolver = null)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.secretResolver = secretResolver;
    }

    public MessageChannel Channel => MessageChannel.Slack;

    public async Task SendAsync(MessageRecipient recipient, MessageRequest request, Guid notificationId, CancellationToken cancellationToken = default)
    {
        string token = await ResolveTokenAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(recipient.SlackUserId))
        {
            throw new InvalidOperationException("Recipient has no Slack user ID.");
        }

        string channelId = await OpenDmChannelAsync(recipient.SlackUserId, token, cancellationToken).ConfigureAwait(false);
        await PostMessageAsync(channelId, BuildMessage(request), token, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> ResolveTokenAsync(CancellationToken cancellationToken)
    {
        if (secretResolver is not null && !string.IsNullOrWhiteSpace(options.BotTokenSecretName))
        {
            string? secret = await secretResolver.GetSecretAsync(options.BotTokenSecretName, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(secret))
            {
                return secret;
            }
        }

        if (!string.IsNullOrWhiteSpace(options.BotToken))
        {
            return options.BotToken;
        }

        throw new InvalidOperationException("Slack bot credential is unavailable.");
    }

    private async Task<string> OpenDmChannelAsync(string slackUserId, string token, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(HttpMethod.Post, "https://slack.com/api/conversations.open", token);
        request.Content = CreateJsonContent(new { users = slackUserId });
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using JsonDocument document = JsonDocument.Parse(body);
        EnsureSlackSuccess(document.RootElement);
        string? channelId = document.RootElement.GetProperty("channel").GetProperty("id").GetString();
        return !string.IsNullOrWhiteSpace(channelId) ? channelId : throw new InvalidOperationException("Slack returned no DM channel ID.");
    }

    private async Task PostMessageAsync(string channelId, string text, string token, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(HttpMethod.Post, "https://slack.com/api/chat.postMessage", token);
        request.Content = CreateJsonContent(new { channel = channelId, text });
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using JsonDocument document = JsonDocument.Parse(body);
        EnsureSlackSuccess(document.RootElement);
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string uri, string token)
    {
        HttpRequestMessage request = new(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static StringContent CreateJsonContent(object value) => new(JsonSerializer.Serialize(value, JsonOptions), Encoding.UTF8, "application/json");

    private static string BuildMessage(MessageRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Title)) return request.Body;
        return string.IsNullOrWhiteSpace(request.Body) ? request.Title : $"*{request.Title}*\n{request.Body}";
    }

    private static void EnsureSlackSuccess(JsonElement root)
    {
        if (root.TryGetProperty("ok", out JsonElement ok) && ok.GetBoolean()) return;
        throw new InvalidOperationException("Slack API operation failed.");
    }
}
