namespace Common.Messaging.UnitTests;

using System.Net;
using System.Text;
using System.Text.Json;
using Common.Messaging.Channels.MicrosoftGraph;
using Xunit;

public sealed class MicrosoftGraphEmailPayloadTests
{
    [Fact]
    public async Task PlainTextAndAttachmentsReachGraphIncludingSenderAsRecipient()
    {
        PayloadHandler handler = new();
        MicrosoftGraphEmailMessageChannel channel = CreateChannel(handler);
        byte[] report = [0, 1, 2, 255];
        await channel.SendAsync(
              new MessageRecipient("manager", "Manager", "sender@example.org")
            , new MessageRequest
            {
                  Title = "Daily Sales"
                , Body = "First line\n<not html> & second line"
                , BodyIsHtml = false
                , Attachments = [new MessageAttachment("sales.pdf", "application/pdf", report)]
            }
            , Guid.NewGuid());

        using JsonDocument document = JsonDocument.Parse(Assert.IsType<string>(handler.Payload));
        JsonElement message = document.RootElement.GetProperty("message");
        Assert.Equal("Text", message.GetProperty("body").GetProperty("contentType").GetString());
        Assert.Equal("First line\n<not html> & second line", message.GetProperty("body").GetProperty("content").GetString());
        JsonElement attachment = message.GetProperty("attachments")[0];
        Assert.Equal("#microsoft.graph.fileAttachment", attachment.GetProperty("@odata.type").GetString());
        Assert.Equal("sales.pdf", attachment.GetProperty("name").GetString());
        Assert.Equal("application/pdf", attachment.GetProperty("contentType").GetString());
        Assert.Equal(report, attachment.GetProperty("contentBytes").GetBytesFromBase64());
        Assert.True(document.RootElement.GetProperty("saveToSentItems").GetBoolean());
    }

    [Fact]
    public async Task ReplyToMetadataReachesGraphWithoutChangingSharedSender()
    {
        PayloadHandler handler = new();
        MicrosoftGraphEmailMessageChannel channel = CreateChannel(handler);
        await channel.SendAsync(
            new MessageRecipient("client", "Client", "client@example.org"),
            new MessageRequest
            {
                Title = "Studio message",
                Body = "Hello",
                Metadata = new Dictionary<string,string>
                {
                    ["ReplyTo"] = "fwright@bryanstudiosltd.com"
                }
            },
            Guid.NewGuid());

        using JsonDocument document = JsonDocument.Parse(Assert.IsType<string>(handler.Payload));
        JsonElement message = document.RootElement.GetProperty("message");
        Assert.Equal(
            "fwright@bryanstudiosltd.com",
            message.GetProperty("replyTo")[0].GetProperty("emailAddress").GetProperty("address").GetString());
    }

    [Fact]
    public async Task InvalidReplyToMetadataFailsBeforeGraphSend()
    {
        PayloadHandler handler = new();
        MicrosoftGraphEmailMessageChannel channel = CreateChannel(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => channel.SendAsync(
            new MessageRecipient("client", "Client", "client@example.org"),
            new MessageRequest
            {
                Body = "Hello",
                Metadata = new Dictionary<string,string> { ["ReplyTo"] = "not-an-email" }
            },
            Guid.NewGuid()));
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task OversizedAttachmentsFailBeforeSending()
    {
        PayloadHandler handler = new();
        MicrosoftGraphEmailMessageChannel channel = CreateChannel(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => channel.SendAsync(
              new MessageRecipient("1", "User", "person@example.org")
            , new MessageRequest { Attachments = [new MessageAttachment("large.pdf", "application/pdf", new byte[2_500_001])] }
            , Guid.NewGuid()));
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task GraphFailureIsNotReportedAsSuccess()
    {
        PayloadHandler handler = new() { SendStatus = HttpStatusCode.Forbidden };
        await Assert.ThrowsAsync<HttpRequestException>(() => CreateChannel(handler).SendAsync(
              new MessageRecipient("1", "User", "person@example.org")
            , new MessageRequest { Body = "Report" }
            , Guid.NewGuid()));
    }

    [Fact]
    public void DurableRequestRoundTripPreservesAttachmentAndBodyFormat()
    {
        MessageRequest request = new()
        {
              BodyIsHtml = false
            , Attachments = [new MessageAttachment("report.csv", "text/csv", [1, 2, 3])]
        };
        MessageRequest restored = JsonSerializer.Deserialize<MessageRequest>(JsonSerializer.Serialize(request))!;
        Assert.False(restored.BodyIsHtml);
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.Single(restored.Attachments).Content);
        Assert.True(new MessageRequest().BodyIsHtml);
    }

    private static MicrosoftGraphEmailMessageChannel CreateChannel(PayloadHandler handler)
        => new(new HttpClient(handler), new MicrosoftGraphEmailOptions
        {
              TenantId = "tenant"
            , ClientId = "client"
            , ClientSecret = "test-only-secret"
            , SenderUpn = "sender@example.org"
            , SkipSenderRecipient = false
        });

    private sealed class PayloadHandler : HttpMessageHandler
    {
        public string? Payload { get; private set; }
        public int Requests { get; private set; }
        public HttpStatusCode SendStatus { get; init; } = HttpStatusCode.Accepted;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (request.RequestUri!.Host == "login.microsoftonline.com")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"access_token\":\"test-token\"}", Encoding.UTF8, "application/json")
                };
            }
            Payload = await request.Content!.ReadAsStringAsync(cancellationToken);
            Assert.Equal("test-token", request.Headers.Authorization?.Parameter);
            return new HttpResponseMessage(SendStatus) { Content = new StringContent("{}") };
        }
    }
}
