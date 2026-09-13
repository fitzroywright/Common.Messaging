using System.Net;
using System.Text.Json;
using Common.Messaging.Channels.Slack;
using Xunit;

namespace Common.Messaging.UnitTests;

public sealed class SlackBusinessChannelTests
{
    [Fact]
    public async Task SendAsync_PostsDirectlyToConfiguredBusinessChannel()
    {
        var handler = new CapturingHandler();
        var channel = new SlackMessageChannel(
            new HttpClient(handler),
            new SlackMessageOptions { BotToken = "test-token" });

        var recipient = new MessageRecipient("pro-1", "Business User");
        var request = new MessageRequest
        {
            Title = "New Ebolito Engagement",
            Body = "A customer wants to discuss a project.",
            Channels = MessageChannel.Slack,
            Metadata = new Dictionary<string, string>
            {
                [SlackMessageChannel.ChannelMetadataKey] = "C0123456789"
            }
        };

        await channel.SendAsync(recipient, request, Guid.NewGuid());

        var sent = Assert.Single(handler.Requests);
        Assert.Equal("https://slack.com/api/chat.postMessage", sent.Uri);
        using var document = JsonDocument.Parse(sent.Body);
        Assert.Equal("C0123456789", document.RootElement.GetProperty("channel").GetString());
        Assert.Contains("New Ebolito Engagement", document.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task SendAsync_StillUsesDmWhenNoBusinessChannelMetadataExists()
    {
        var handler = new CapturingHandler();
        var channel = new SlackMessageChannel(
            new HttpClient(handler),
            new SlackMessageOptions { BotToken = "test-token" });

        var recipient = new MessageRecipient("pro-2", "Individual User", SlackUserId: "U0123456789");
        var request = new MessageRequest { Title = "Hello", Body = "World", Channels = MessageChannel.Slack };

        await channel.SendAsync(recipient, request, Guid.NewGuid());

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("https://slack.com/api/conversations.open", handler.Requests[0].Uri);
        Assert.Equal("https://slack.com/api/chat.postMessage", handler.Requests[1].Uri);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(request.RequestUri!.ToString(), body));

            if (request.RequestUri!.AbsolutePath.EndsWith("/conversations.open", StringComparison.Ordinal))
            {
                return Json("{\"ok\":true,\"channel\":{\"id\":\"D0123456789\"}}");
            }

            return Json("{\"ok\":true}");
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };
    }

    private sealed record CapturedRequest(string Uri, string Body);
}
