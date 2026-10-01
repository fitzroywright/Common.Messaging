using System.Net;
using System.Text.Json;
using Common.Messaging.Channels.WhatsApp;
using Xunit;

namespace Common.Messaging.UnitTests;

public sealed class WhatsAppMessageChannelTests
{
    [Fact]
    public async Task SendAsync_PostsMetaCloudApiTextMessage()
    {
        var handler = new CapturingHandler();
        var channel = new WhatsAppMessageChannel(
            new HttpClient(handler),
            new WhatsAppMessageOptions
            {
                Enabled = true,
                GraphApiVersion = "v23.0",
                PhoneNumberId = "123456789",
                AccessToken = "test-token"
            });

        var recipient = new MessageRecipient("user-1", "Customer", Mobile: "+18765551212", PreferredChannels: MessageChannel.WhatsApp);
        var request = new MessageRequest
        {
            Title = "New Ebolito Engagement",
            Body = "Please review this request.",
            Channels = MessageChannel.WhatsApp,
            CorrelationId = "engagement-123",
            Metadata = new Dictionary<string, string> { ["ebolito.engagementId"] = "engagement-123" }
        };
        var notificationId = Guid.NewGuid();

        await channel.SendAsync(recipient, request, notificationId);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal("https://graph.facebook.com/v23.0/123456789/messages", sent.Uri);
        Assert.Equal("Bearer test-token", sent.Authorization);
        using var document = JsonDocument.Parse(sent.Body);
        Assert.Equal("whatsapp", document.RootElement.GetProperty("messaging_product").GetString());
        Assert.Equal("+18765551212", document.RootElement.GetProperty("to").GetString());
        Assert.Equal("text", document.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task SendAsync_RequiresMobileNumber()
    {
        var channel = new WhatsAppMessageChannel(
            new HttpClient(new CapturingHandler()),
            new WhatsAppMessageOptions { Enabled = true, PhoneNumberId = "123456789", AccessToken = "test-token" });

        var recipient = new MessageRecipient("user-2", "No Mobile");
        var request = new MessageRequest { Body = "Hello", Channels = MessageChannel.WhatsApp };

        await Assert.ThrowsAsync<InvalidOperationException>(() => channel.SendAsync(recipient, request, Guid.NewGuid()));
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new CapturedRequest(
                request.RequestUri!.ToString(),
                request.Headers.Authorization?.ToString(),
                body));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed record CapturedRequest(string Uri, string? Authorization, string Body);
}
