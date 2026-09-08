namespace Common.Messaging.Channels.Slack;

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

public sealed class SlackMessageChannel : IExternalMessageChannel
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient httpClient;
    private readonly SlackMessageOptions options;

    public SlackMessageChannel(HttpClient httpClient, SlackMessageOptions options)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public MessageChannel Channel => MessageChannel.Slack;

    public async Task SendAsync(
        MessageRecipient recipient,
        MessageRequest request,
        Guid notificationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.BotToken))
        {
            throw new InvalidOperationException("Slack bot token is not configured.");
        }

        if (string.IsNullOrWhiteSpace(recipient.SlackUserId))
        {
            throw new InvalidOperationException($"Recipient '{recipient.UserName}' has no Slack user ID.");
        }

        string channelId = await OpenDmChannelAsync(recipient.SlackUserId, cancellationToken);
        string text = BuildMessage(request);
        await PostMessageAsync(channelId, text, cancellationToken);
    }

    private async Task<string> OpenDmChannelAsync(string slackUserId, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(HttpMethod.Post, "https://slack.com/api/conversations.open");
        request.Content = CreateJsonContent(new { users = slackUserId });

        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        using JsonDocument document = JsonDocument.Parse(body);
        EnsureSlackSuccess(document.RootElement, "conversations.open");

        string? channelId = document.RootElement.GetProperty("channel").GetProperty("id").GetString();
        return !string.IsNullOrWhiteSpace(channelId)
            ? channelId
            : throw new InvalidOperationException("Slack returned no DM channel ID.");
    }

    private async Task PostMessageAsync(string channelId, string text, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(HttpMethod.Post, "https://slack.com/api/chat.postMessage");
        request.Content = CreateJsonContent(new { channel = channelId, text });

        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        using JsonDocument document = JsonDocument.Parse(body);
        EnsureSlackSuccess(document.RootElement, "chat.postMessage");
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string uri)
    {
        HttpRequestMessage request = new(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.BotToken);
        return request;
    }

    private static StringContent CreateJsonContent(object value)
    {
        return new StringContent(JsonSerializer.Serialize(value, JsonOptions), Encoding.UTF8, "application/json");
    }

    private static string BuildMessage(MessageRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            return request.Body;
        }

        return string.IsNullOrWhiteSpace(request.Body)
            ? request.Title
            : $"*{request.Title}*\n{request.Body}";
    }

    private static void EnsureSlackSuccess(JsonElement root, string operation)
    {
        if (root.TryGetProperty("ok", out JsonElement ok) && ok.GetBoolean())
        {
            return;
        }

        string error = root.TryGetProperty("error", out JsonElement errorElement)
            ? errorElement.GetString() ?? "unknown_error"
            : "unknown_error";

        throw new InvalidOperationException($"Slack {operation} failed: {error}");
    }
}
