namespace Common.Messaging.Channels.MicrosoftGraph;

using System.Net.Http.Headers;
using System.Text.Json;

public sealed class MicrosoftGraphRecipientDirectory : IEmailRecipientDirectory
{
    private readonly HttpClient http;
    private readonly MicrosoftGraphEmailOptions options;
    private readonly IChannelSecretResolver secrets;
    private readonly Dictionary<string, (DateTimeOffset Expires, EmailGroupMembership Value)> cache = new();
    private readonly SemaphoreSlim gate = new(1, 1);

    public MicrosoftGraphRecipientDirectory(HttpClient http, MicrosoftGraphEmailOptions options, IChannelSecretResolver secrets)
    { this.http = http; this.options = options; this.secrets = secrets; }

    public async Task<EmailGroupMembership> GetGroupAsync(string providerReference, CancellationToken cancellationToken = default)
    {
        // IDs, not arbitrary URLs: do not leak a provider token to a registered address.
        if (!Guid.TryParse(providerReference, out Guid id)) return new(providerReference, "", false, [], "A Microsoft 365 group object ID is required.");
        string key = id.ToString("D");
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (cache.TryGetValue(key, out var cached) && cached.Expires > DateTimeOffset.UtcNow) return cached.Value;
            string secret = await secrets.GetSecretAsync(options.ClientSecretName, cancellationToken) ?? options.ClientSecret;
            using var tokenResponse = await http.PostAsync($"https://login.microsoftonline.com/{Uri.EscapeDataString(options.TenantId)}/oauth2/v2.0/token",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["client_id"] = options.ClientId, ["client_secret"] = secret,
                    ["scope"] = "https://graph.microsoft.com/.default", ["grant_type"] = "client_credentials" }), cancellationToken);
            tokenResponse.EnsureSuccessStatusCode();
            using var tokenJson = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync(cancellationToken));
            string token = tokenJson.RootElement.GetProperty("access_token").GetString()!;
            using var group = await GetAsync($"https://graph.microsoft.com/v1.0/groups/{key}?$select=id,mail,mailEnabled,groupTypes,visibility", token, cancellationToken);
            var g = group.RootElement;
            string address = g.TryGetProperty("mail", out var mail) ? mail.GetString() ?? "" : "";
            bool dynamic = g.GetProperty("groupTypes").EnumerateArray().Any(t => t.GetString() == "DynamicMembership");
            bool hidden = g.TryGetProperty("visibility", out var visibility) && visibility.GetString() == "HiddenMembership";
            if (!g.GetProperty("mailEnabled").GetBoolean() || dynamic || hidden || string.IsNullOrWhiteSpace(address))
                return new(key, address, false, [], "Dynamic, hidden-membership or non-mail-enabled groups require controlled handling.");
            List<string> members = [];
            string? url = $"https://graph.microsoft.com/v1.0/groups/{key}/members?$select=id,mail,userPrincipalName";
            HashSet<string> visited = new(StringComparer.Ordinal);
            while (url is not null)
            {
                if (!visited.Add(url) || visited.Count > 100) return new(key, address, false, [], "Membership paging could not be completed.");
                using var page = await GetAsync(url, token, cancellationToken);
                foreach (var member in page.RootElement.GetProperty("value").EnumerateArray())
                {
                    string? type = member.TryGetProperty("@odata.type", out var t) ? t.GetString() : null;
                    if (type != "#microsoft.graph.user" && type != "#microsoft.graph.orgContact")
                        return new(key, address, false, [], "Nested groups or unsupported member objects require review.");
                    string? memberAddress = member.TryGetProperty("mail", out var m) ? m.GetString() : null;
                    if (string.IsNullOrWhiteSpace(memberAddress)) return new(key, address, false, [], "A member email address is missing or inaccessible.");
                    members.Add(memberAddress.Trim().ToLowerInvariant());
                }
                url = page.RootElement.TryGetProperty("@odata.nextLink", out var next) ? next.GetString() : null;
            }
            var result = new EmailGroupMembership(key, address, true, members.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
            cache[key] = (DateTimeOffset.UtcNow.AddMinutes(2), result);
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException)
        { return new(key, "", false, [], "Provider membership discovery failed: " + ex.GetType().Name); }
        catch(OperationCanceledException) when(!cancellationToken.IsCancellationRequested)
        { return new(key,"",false,[],"Provider membership discovery timed out; review is required."); }
        finally { gate.Release(); }
    }

    private async Task<JsonDocument> GetAsync(string url, string token, CancellationToken cancellationToken)
    {
        var uri = new Uri(url);
        if (uri.Scheme != "https" || uri.Host != "graph.microsoft.com" || !uri.AbsolutePath.StartsWith("/v1.0/groups/", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected Microsoft Graph membership URL.");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }
}
