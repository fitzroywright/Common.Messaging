namespace Common.Messaging.UnitTests;

using System.Net;
using Common.Messaging.Channels.MicrosoftGraph;
using Xunit;

public sealed class MicrosoftGraphMailboxAttachmentTests
{
    [Fact]
    public async Task AttachmentPagesImportFileBytesWithoutInvalidSelect()
    {
        var handler = new Handler();
        var batch = await Create(handler).GetInboxChangesAsync(null);
        Assert.Equal("checkpoint", batch.DeltaLink);
        var message = Assert.Single(batch.Messages);
        Assert.Equal(2, message.FileAttachments.Count);
        Assert.Equal(new byte[] { 1, 2, 3 }, message.FileAttachments[0].Content);
        Assert.True(message.FileAttachments[1].IsInline);
        Assert.Equal(2, handler.AttachmentRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AttachmentFailurePreservesStatusAndSafeIdentifiersWithoutReturningCheckpoint(bool nonJson)
    {
        var handler = new Handler { Fail = true, NonJson = nonJson };
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Create(handler).GetInboxChangesAsync("https://graph.microsoft.com/v1.0/delta"));
        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.Contains("RequestId=test-request-id", error.Message);
        Assert.Contains(nonJson ? "GraphCode=Unavailable" : "GraphCode=BadRequest", error.Message);
        Assert.DoesNotContain("private-provider-message", error.Message);
        Assert.DoesNotContain("test-token", error.Message);
    }

    private static MicrosoftGraphMailboxClient Create(Handler handler) => new(new HttpClient(handler),
        new() { InboundEnabled = true, TenantId = "tenant", ClientId = "client", ClientSecret = "test-secret", SenderUpn = "hub@studio.test" });

    private sealed class Handler : HttpMessageHandler
    {
        public bool Fail { get; init; }
        public bool NonJson { get; init; }
        public int AttachmentRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.Host == "login.microsoftonline.com") return Response("{\"access_token\":\"test-token\"}");
            Assert.Equal("graph.microsoft.com", uri.Host);
            if (!uri.AbsolutePath.EndsWith("/attachments"))
                return Response("""
                    {"value":[{"id":"provider-id","internetMessageId":"internet-id","subject":"Test","from":{"emailAddress":{"address":"client@outside.test"}},"toRecipients":[{"emailAddress":{"address":"hub@studio.test"}}],"body":{"content":"Test body"},"hasAttachments":true}],"@odata.deltaLink":"checkpoint"}
                    """);

            AttachmentRequests++;
            Assert.DoesNotContain("$select", Uri.UnescapeDataString(uri.Query));
            if (Fail)
            {
                var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(NonJson ? "private-provider-message" :
                        """{"error":{"code":"BadRequest","message":"private-provider-message"}}""")
                };
                response.Headers.Add("request-id", "test-request-id");
                return Task.FromResult(response);
            }
            if (uri.Query.Contains("skiptoken"))
                return Response("""
                    {"value":[{"@odata.type":"#microsoft.graph.fileAttachment","name":"inline.png","contentType":"image/png","isInline":true,"contentBytes":"BAUG"}]}
                    """);
            return Response("""
                {"value":[{"@odata.type":"#microsoft.graph.fileAttachment","name":"test.bin","contentType":"application/octet-stream","contentBytes":"AQID"},{"@odata.type":"#microsoft.graph.itemAttachment","name":"forwarded message"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/users/hub/messages/provider-id/attachments?$skiptoken=next"}
                """);
        }

        private static Task<HttpResponseMessage> Response(string body) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }
}
