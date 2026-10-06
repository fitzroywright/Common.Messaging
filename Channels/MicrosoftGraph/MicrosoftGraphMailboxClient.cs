namespace Common.Messaging.Channels.MicrosoftGraph;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

public sealed class MicrosoftGraphMailboxClient
{
    private readonly HttpClient httpClient;
    private readonly MicrosoftGraphEmailOptions options;
    private readonly IChannelSecretResolver? secretResolver;

    public MicrosoftGraphMailboxClient(HttpClient httpClient, MicrosoftGraphEmailOptions options, IChannelSecretResolver? secretResolver = null)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.secretResolver = secretResolver;
    }

    public bool IsConfigured =>
        options.InboundEnabled &&
        !string.IsNullOrWhiteSpace(options.TenantId) &&
        !string.IsNullOrWhiteSpace(options.ClientId) &&
        !string.IsNullOrWhiteSpace(options.SenderUpn);

    public TimeSpan PollInterval => TimeSpan.FromSeconds(Math.Max(5, options.InboundPollSeconds));

    public async Task<MicrosoftGraphMailboxVerification> VerifyConfigurationAsync(CancellationToken cancellationToken = default)
    {
        bool tenant = !string.IsNullOrWhiteSpace(options.TenantId);
        bool client = !string.IsNullOrWhiteSpace(options.ClientId);
        bool mailbox = !string.IsNullOrWhiteSpace(options.SenderUpn);
        if (!options.InboundEnabled)
            return new("Disabled", false, false, tenant, client, false, mailbox, null, "Microsoft Graph inbound email is disabled.");

        string secret = await ResolveSecretAsync(cancellationToken).ConfigureAwait(false);
        bool secretPresent = !string.IsNullOrWhiteSpace(secret);
        if (!tenant || !client || !mailbox || !secretPresent)
            return new("Incomplete", false, false, tenant, client, secretPresent, mailbox, null, "One or more required Microsoft Graph settings are missing.");

        string token;
        try { token = await AcquireAppTokenAsync(secret, cancellationToken).ConfigureAwait(false); }
        catch (HttpRequestException ex)
        {
            return new("Authentication failed", false, false, true, true, true, true, ex.StatusCode.HasValue ? (int)ex.StatusCode.Value : null, "Microsoft Entra ID rejected the configured tenant/client/secret credentials.");
        }

        string url = $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(options.SenderUpn.Trim())}/mailFolders/inbox?$select=id,displayName,totalItemCount,unreadItemCount";
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return new("Mailbox access failed", true, false, true, true, true, true, (int)response.StatusCode, $"Microsoft Graph returned {(int)response.StatusCode} {response.ReasonPhrase} while opening the configured Inbox.");

        return new("Verified", true, true, true, true, true, true, (int)response.StatusCode, "Credentials are valid and the configured mailbox Inbox is accessible.");
    }

    public async Task<MicrosoftGraphInboxDeltaBatch> GetInboxChangesAsync(string? deltaLink, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) return new([], deltaLink);

        string secret = await ResolveSecretAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(secret)) throw new InvalidOperationException("Microsoft Graph client secret is unavailable.");
        string token = await AcquireAppTokenAsync(secret, cancellationToken).ConfigureAwait(false);
        string? nextUrl = string.IsNullOrWhiteSpace(deltaLink) ? BuildInitialDeltaUrl() : deltaLink;
        string? completedDeltaLink = deltaLink;
        List<MicrosoftGraphInboundMessage> messages = [];

        while (!string.IsNullOrWhiteSpace(nextUrl))
        {
            using HttpRequestMessage request = new(HttpMethod.Get, nextUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation("Prefer", "outlook.body-content-type=\"text\"");
            using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Gone && !string.IsNullOrWhiteSpace(deltaLink))
                throw new MicrosoftGraphDeltaTokenExpiredException();
            response.EnsureSuccessStatusCode();

            GraphMessageDeltaPage? page = await response.Content.ReadFromJsonAsync<GraphMessageDeltaPage>(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (page is null) throw new InvalidOperationException("Microsoft Graph returned an empty delta response.");

            foreach (GraphMessage message in page.Value.Where(x => x.Removed is null).Where(IsUsable))
            {
                IReadOnlyList<MicrosoftGraphInboundAttachment> attachments = message.HasAttachments
                    ? await GetAttachmentsAsync(token, message.Id!, cancellationToken).ConfigureAwait(false)
                    : [];
                messages.Add(ToInboundMessage(message, attachments));
            }

            if (!string.IsNullOrWhiteSpace(page.NextLink)) { nextUrl = page.NextLink; continue; }
            if (!string.IsNullOrWhiteSpace(page.DeltaLink)) completedDeltaLink = page.DeltaLink;
            nextUrl = null;
        }

        return new(messages, completedDeltaLink);
    }

    private async Task<string> ResolveSecretAsync(CancellationToken cancellationToken)
    {
        if (secretResolver is not null && !string.IsNullOrWhiteSpace(options.ClientSecretName))
        {
            string? value = await secretResolver.GetSecretAsync(options.ClientSecretName, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return options.ClientSecret;
    }

    private async Task<string> AcquireAppTokenAsync(string secret, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, $"https://login.microsoftonline.com/{Uri.EscapeDataString(options.TenantId.Trim())}/oauth2/v2.0/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string,string>
            {
                ["client_id"] = options.ClientId.Trim(),
                ["client_secret"] = secret,
                ["scope"] = "https://graph.microsoft.com/.default",
                ["grant_type"] = "client_credentials"
            })
        };
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        GraphTokenResponse? token = await response.Content.ReadFromJsonAsync<GraphTokenResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(token?.AccessToken)
            ? throw new InvalidOperationException("Microsoft Graph did not return an access token.")
            : token.AccessToken;
    }

    private string BuildInitialDeltaUrl()
    {
        int pageSize = Math.Clamp(options.InboundPageSize, 1, 100);
        string mailbox = Uri.EscapeDataString(options.SenderUpn.Trim());
        return $"https://graph.microsoft.com/v1.0/users/{mailbox}/mailFolders/inbox/messages/delta?$select=id,conversationId,subject,receivedDateTime,from,toRecipients,ccRecipients,body,internetMessageHeaders,hasAttachments&$top={pageSize}";
    }

    private async Task<IReadOnlyList<MicrosoftGraphInboundAttachment>> GetAttachmentsAsync(string token, string messageId, CancellationToken cancellationToken)
    {
        string mailbox = Uri.EscapeDataString(options.SenderUpn.Trim());
        string? nextUrl = $"https://graph.microsoft.com/v1.0/users/{mailbox}/messages/{Uri.EscapeDataString(messageId)}/attachments?$select=id,name,contentType,isInline,@odata.type,contentBytes";
        List<MicrosoftGraphInboundAttachment> result = [];
        while (!string.IsNullOrWhiteSpace(nextUrl))
        {
            using HttpRequestMessage request = new(HttpMethod.Get, nextUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            GraphAttachmentPage? page = await response.Content.ReadFromJsonAsync<GraphAttachmentPage>(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (page is null) throw new InvalidOperationException("Microsoft Graph returned an empty attachment response.");
            foreach (GraphAttachment a in page.Value)
            {
                if (!string.Equals(a.ODataType, "#microsoft.graph.fileAttachment", StringComparison.OrdinalIgnoreCase) || a.IsInline || string.IsNullOrWhiteSpace(a.ContentBytes)) continue;
                byte[] bytes;
                try { bytes = Convert.FromBase64String(a.ContentBytes); }
                catch (FormatException ex) { throw new InvalidOperationException($"Microsoft Graph returned invalid base64 content for attachment '{a.Name}'.", ex); }
                if (bytes.Length > 0) result.Add(new(string.IsNullOrWhiteSpace(a.Name) ? "attachment.bin" : a.Name, string.IsNullOrWhiteSpace(a.ContentType) ? "application/octet-stream" : a.ContentType, bytes, false));
            }
            nextUrl = page.NextLink;
        }
        return result;
    }

    private static bool IsUsable(GraphMessage m) =>
        !string.IsNullOrWhiteSpace(m.Id) && !string.IsNullOrWhiteSpace(m.From?.EmailAddress?.Address) &&
        (m.ToRecipients.Count > 0 || m.CcRecipients.Count > 0) && !string.IsNullOrWhiteSpace(m.Subject) && !string.IsNullOrWhiteSpace(m.Body?.Content);

    private static MicrosoftGraphInboundMessage ToInboundMessage(GraphMessage m, IReadOnlyList<MicrosoftGraphInboundAttachment> attachments)
    {
        string address = m.From!.EmailAddress!.Address!;
        return new(m.Id!, m.ConversationId, string.IsNullOrWhiteSpace(m.From.EmailAddress.Name) ? address : m.From.EmailAddress.Name!,
            address, Addresses(m.ToRecipients), Addresses(m.CcRecipients),
            m.InternetMessageHeaders.Where(x => !string.IsNullOrWhiteSpace(x.Name)).GroupBy(x => x.Name!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Last().Value ?? string.Empty, StringComparer.OrdinalIgnoreCase),
            m.Subject!, m.Body!.Content, m.ReceivedDateTime ?? DateTimeOffset.UtcNow, attachments);
    }

    private static IReadOnlyList<string> Addresses(IEnumerable<GraphRecipient> values) =>
        values.Select(x => x.EmailAddress?.Address).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToList();

    private sealed class GraphTokenResponse { [JsonPropertyName("access_token")] public string? AccessToken { get; init; } }
    private sealed class GraphMessageDeltaPage { [JsonPropertyName("value")] public List<GraphMessage> Value { get; init; } = []; [JsonPropertyName("@odata.nextLink")] public string? NextLink { get; init; } [JsonPropertyName("@odata.deltaLink")] public string? DeltaLink { get; init; } }
    private sealed class GraphAttachmentPage { [JsonPropertyName("value")] public List<GraphAttachment> Value { get; init; } = []; [JsonPropertyName("@odata.nextLink")] public string? NextLink { get; init; } }
    private sealed class GraphAttachment { [JsonPropertyName("@odata.type")] public string? ODataType { get; init; } [JsonPropertyName("name")] public string? Name { get; init; } [JsonPropertyName("contentType")] public string? ContentType { get; init; } [JsonPropertyName("isInline")] public bool IsInline { get; init; } [JsonPropertyName("contentBytes")] public string? ContentBytes { get; init; } }
    private sealed class GraphMessage { [JsonPropertyName("id")] public string? Id { get; init; } [JsonPropertyName("conversationId")] public string? ConversationId { get; init; } [JsonPropertyName("subject")] public string? Subject { get; init; } [JsonPropertyName("receivedDateTime")] public DateTimeOffset? ReceivedDateTime { get; init; } [JsonPropertyName("from")] public GraphRecipient? From { get; init; } [JsonPropertyName("toRecipients")] public List<GraphRecipient> ToRecipients { get; init; } = []; [JsonPropertyName("ccRecipients")] public List<GraphRecipient> CcRecipients { get; init; } = []; [JsonPropertyName("internetMessageHeaders")] public List<GraphHeader> InternetMessageHeaders { get; init; } = []; [JsonPropertyName("body")] public GraphBody? Body { get; init; } [JsonPropertyName("hasAttachments")] public bool HasAttachments { get; init; } [JsonPropertyName("@removed")] public object? Removed { get; init; } }
    private sealed class GraphHeader { [JsonPropertyName("name")] public string? Name { get; init; } [JsonPropertyName("value")] public string? Value { get; init; } }
    private sealed class GraphRecipient { [JsonPropertyName("emailAddress")] public GraphAddress? EmailAddress { get; init; } }
    private sealed class GraphAddress { [JsonPropertyName("name")] public string? Name { get; init; } [JsonPropertyName("address")] public string? Address { get; init; } }
    private sealed class GraphBody { [JsonPropertyName("content")] public string Content { get; init; } = string.Empty; }
}

public sealed record MicrosoftGraphInboxDeltaBatch(IReadOnlyList<MicrosoftGraphInboundMessage> Messages, string? DeltaLink);
public sealed record MicrosoftGraphInboundMessage(string MessageId, string? ConversationId, string FromName, string FromAddress, IReadOnlyList<string> ToAddresses, IReadOnlyList<string> CcAddresses, IReadOnlyDictionary<string,string> Headers, string Subject, string Body, DateTimeOffset ReceivedAtUtc, IReadOnlyList<MicrosoftGraphInboundAttachment> FileAttachments);
public sealed record MicrosoftGraphInboundAttachment(string FileName, string ContentType, byte[] Content, bool IsInline);
public sealed record MicrosoftGraphMailboxVerification(string State, bool AuthenticationSucceeded, bool MailboxAccessible, bool TenantConfigured, bool ClientConfigured, bool ClientSecretConfigured, bool MailboxConfigured, int? GraphStatusCode, string Detail);
public sealed class MicrosoftGraphDeltaTokenExpiredException : Exception
{
    public MicrosoftGraphDeltaTokenExpiredException() : base("The Microsoft Graph delta token expired and a fresh synchronization is required.") { }
}
