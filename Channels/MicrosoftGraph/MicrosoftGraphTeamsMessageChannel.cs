namespace Common.Messaging.Channels.MicrosoftGraph;

using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

public sealed class MicrosoftGraphTeamsMessageChannel : IExternalMessageChannel
{
    private readonly HttpClient httpClient;
    private readonly MicrosoftGraphTeamsOptions options;
    private readonly IChannelSecretResolver? secretResolver;
    private readonly ConcurrentDictionary<string, string> chatIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> userIds = new(StringComparer.OrdinalIgnoreCase);
    private string? senderId;
    private readonly SemaphoreSlim senderLock = new(1, 1);

    public MicrosoftGraphTeamsMessageChannel(
        HttpClient httpClient,
        MicrosoftGraphTeamsOptions options,
        IChannelSecretResolver? secretResolver = null)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.secretResolver = secretResolver;
    }

    public MessageChannel Channel => MessageChannel.MsTeams;

    public async Task SendAsync(
        MessageRecipient recipient,
        MessageRequest request,
        Guid notificationId,
        CancellationToken cancellationToken = default)
    {
        string recipientUpn = recipient.Email?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(recipientUpn))
            throw new InvalidOperationException("Microsoft Graph Teams delivery requires the recipient email/UPN.");
        if (string.IsNullOrWhiteSpace(options.SenderUpn))
            throw new InvalidOperationException("Microsoft Graph Teams SenderUpn is required.");

        string token = await ResolveTokenAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Microsoft Graph Teams delegated access token is unavailable.");

        string currentSenderId = await GetSenderIdAsync(token, cancellationToken).ConfigureAwait(false);
        string recipientId = await GetUserIdAsync(recipientUpn, token, cancellationToken).ConfigureAwait(false);
        if (string.Equals(currentSenderId, recipientId, StringComparison.OrdinalIgnoreCase))
            return;

        string chatId = await GetOrCreateChatIdAsync(currentSenderId, recipientUpn, recipientId, token, cancellationToken).ConfigureAwait(false);
        await SendMessageAsync(chatId, request.Body ?? request.Title ?? string.Empty, token, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> ResolveTokenAsync(CancellationToken cancellationToken)
    {
        if (secretResolver is not null && !string.IsNullOrWhiteSpace(options.DelegatedAccessTokenSecretName))
        {
            string? secret = await secretResolver.GetSecretAsync(options.DelegatedAccessTokenSecretName, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(secret)) return secret;
        }
        return options.DelegatedAccessToken;
    }

    private async Task<string> GetSenderIdAsync(string token, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(senderId)) return senderId;
        await senderLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrWhiteSpace(senderId)) return senderId;
            using JsonDocument document = await SendGraphForJsonAsync(HttpMethod.Get, "https://graph.microsoft.com/v1.0/me?$select=id,userPrincipalName", null, token, cancellationToken).ConfigureAwait(false);
            string resolvedUpn = RequiredString(document.RootElement, "userPrincipalName", "Microsoft Graph did not return the Teams sender UPN.");
            if (!string.Equals(resolvedUpn, options.SenderUpn.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Microsoft Graph Teams token belongs to '{resolvedUpn}', not configured sender '{options.SenderUpn.Trim()}'.");
            senderId = RequiredString(document.RootElement, "id", "Microsoft Graph did not return the Teams sender identity.");
            return senderId;
        }
        finally
        {
            senderLock.Release();
        }
    }

    private async Task<string> GetUserIdAsync(string upn, string token, CancellationToken cancellationToken)
    {
        if (userIds.TryGetValue(upn, out string? cached)) return cached;
        using JsonDocument document = await SendGraphForJsonAsync(
            HttpMethod.Get,
            $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(upn)}?$select=id",
            null,
            token,
            cancellationToken).ConfigureAwait(false);
        string id = RequiredString(document.RootElement, "id", $"Microsoft Graph user was not found: {upn}");
        userIds[upn] = id;
        return id;
    }

    private async Task<string> GetOrCreateChatIdAsync(
        string currentSenderId,
        string recipientUpn,
        string recipientId,
        string token,
        CancellationToken cancellationToken)
    {
        if (chatIds.TryGetValue(recipientUpn, out string? cached)) return cached;
        object body = new
        {
            chatType = "oneOnOne",
            members = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["@odata.type"] = "#microsoft.graph.aadUserConversationMember",
                    ["roles"] = new[] { "owner" },
                    ["user@odata.bind"] = $"https://graph.microsoft.com/v1.0/users('{currentSenderId}')"
                },
                new Dictionary<string, object?>
                {
                    ["@odata.type"] = "#microsoft.graph.aadUserConversationMember",
                    ["roles"] = new[] { "owner" },
                    ["user@odata.bind"] = $"https://graph.microsoft.com/v1.0/users('{recipientId}')"
                }
            }
        };
        using JsonDocument document = await SendGraphForJsonAsync(
            HttpMethod.Post,
            "https://graph.microsoft.com/v1.0/chats",
            body,
            token,
            cancellationToken).ConfigureAwait(false);
        string id = RequiredString(document.RootElement, "id", "Microsoft Graph did not return a Teams chat id.");
        chatIds[recipientUpn] = id;
        return id;
    }

    private async Task SendMessageAsync(string chatId, string htmlBody, string token, CancellationToken cancellationToken)
    {
        object body = new { body = new { contentType = "html", content = htmlBody } };
        using JsonDocument ignored = await SendGraphForJsonAsync(
            HttpMethod.Post,
            $"https://graph.microsoft.com/v1.0/chats/{Uri.EscapeDataString(chatId)}/messages",
            body,
            token,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonDocument> SendGraphForJsonAsync(
        HttpMethod method,
        string uri,
        object? body,
        string token,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Microsoft Graph Teams request failed with HTTP {(int)response.StatusCode}: {Sanitize(json)}");
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
    }

    private static string RequiredString(JsonElement element, string propertyName, string error)
        => element.TryGetProperty(propertyName, out JsonElement value) && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new InvalidOperationException(error);

    private static string Sanitize(string value)
        => string.IsNullOrWhiteSpace(value) ? "No response body." : value.Length <= 512 ? value : value[..512];
}
