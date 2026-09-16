using System.Net;
using System.Text;
using Common.Messaging.Channels.MicrosoftGraph;
using Xunit;

namespace Common.Messaging.UnitTests;

public sealed class MicrosoftGraphChannelTests
{
    [Fact]
    public async Task EmailChannel_UsesAppTokenAndConfiguredSender()
    {
        var handler = new RecordingHandler(request =>
        {
            if (request.RequestUri!.Host == "login.microsoftonline.com")
                return Json(HttpStatusCode.OK, "{\"access_token\":\"token-1\"}");

            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://graph.microsoft.com/v1.0/users/svc%40example.org/sendMail", request.RequestUri!.ToString());
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("token-1", request.Headers.Authorization?.Parameter);
            return Json(HttpStatusCode.Accepted, "{}");
        });

        var channel = new MicrosoftGraphEmailMessageChannel(
            new HttpClient(handler),
            new MicrosoftGraphEmailOptions
            {
                TenantId = "tenant",
                ClientId = "client",
                SenderUpn = "svc@example.org",
                ClientSecretName = "graph-secret"
            },
            new DictionarySecretResolver(new Dictionary<string, string> { ["graph-secret"] = "secret" }));

        await channel.SendAsync(
            new MessageRecipient("1", "User", "person@example.org"),
            new MessageRequest { Title = "Test", Body = "Hello" },
            Guid.NewGuid());

        Assert.Equal(MessageChannel.MsEmail, channel.Channel);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task TeamsChannel_ResolvesUsersCreatesChatAndPostsMessage()
    {
        var handler = new RecordingHandler(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/me", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, "{\"id\":\"sender-id\",\"userPrincipalName\":\"svc@example.org\"}");
            if (path.Contains("/users/", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, "{\"id\":\"recipient-id\"}");
            if (path.EndsWith("/chats", StringComparison.Ordinal))
                return Json(HttpStatusCode.Created, "{\"id\":\"chat-id\"}");
            if (path.EndsWith("/messages", StringComparison.Ordinal))
                return Json(HttpStatusCode.Created, "{\"id\":\"message-id\"}");
            return Json(HttpStatusCode.NotFound, "{}");
        });

        var channel = new MicrosoftGraphTeamsMessageChannel(
            new HttpClient(handler),
            new MicrosoftGraphTeamsOptions
            {
                SenderUpn = "svc@example.org",
                DelegatedAccessTokenSecretName = "teams-token"
            },
            new DictionarySecretResolver(new Dictionary<string, string> { ["teams-token"] = "delegated-token" }));

        await channel.SendAsync(
            new MessageRecipient("1", "User", "person@example.org"),
            new MessageRequest { Body = "Hello Teams" },
            Guid.NewGuid());

        Assert.Equal(MessageChannel.MsTeams, channel.Channel);
        Assert.Equal(4, handler.RequestCount);
        Assert.All(handler.AuthorizationParameters, value => Assert.Equal("delegated-token", value));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class DictionarySecretResolver(IReadOnlyDictionary<string, string> values) : IChannelSecretResolver
    {
        public Task<string?> GetSecretAsync(string secretName, CancellationToken cancellationToken = default)
            => Task.FromResult(values.TryGetValue(secretName, out string? value) ? value : null);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public List<string?> AuthorizationParameters { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            if (request.Headers.Authorization is not null)
                AuthorizationParameters.Add(request.Headers.Authorization.Parameter);
            return Task.FromResult(responder(request));
        }
    }
}
