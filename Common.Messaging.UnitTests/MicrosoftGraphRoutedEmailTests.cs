namespace Common.Messaging.UnitTests;

using System.Net;
using System.Text;
using Common.Messaging.Channels.MicrosoftGraph;
using Xunit;

public sealed class MicrosoftGraphRoutedEmailTests
{
    private static MessageRequest Request()=>new(){Title="Client reply",Body="Message body",BodyIsHtml=false,
        Attachments=[new("notes.txt","text/plain",[1,2,255])],Metadata=new Dictionary<string,string>{["Email.MessageId"]="<stable@studio.test>",
            ["Email.From"]="paul@studio.test",["Email.ReplyTo"]="router@studio.test",["Email.InReplyTo"]="<prior@outside.test>",["Email.References"]="<prior@outside.test>"}};
    [Fact]
    public void MimePreservesAuthorizedFromReplyHeadersStableIdentityAndAttachment()
    {
        string mime=Encoding.UTF8.GetString(Convert.FromBase64String(MicrosoftGraphMimeEnvelope.Build(new("client","Client","client@outside.test"),Request(),Guid.NewGuid(),"router@studio.test")));
        Assert.Contains("From: paul@studio.test\r\n",mime);Assert.Contains("Reply-To: router@studio.test\r\n",mime);
        Assert.Contains("Message-ID: <stable@studio.test>",mime);Assert.Contains("In-Reply-To: <prior@outside.test>",mime);
        Assert.Contains("References: <prior@outside.test>",mime);Assert.Contains("filename*=utf-8''notes.txt",mime);Assert.Contains("AQL/",mime);
    }
    [Fact]
    public async Task TimeoutAfterSendIsAnUnknownOutcomeAndCannotBeBlindlyRetried()
    {
        var handler=new Handler{TimeoutOnSend=true};var channel=Channel(handler);
        await Assert.ThrowsAsync<ExternalDeliveryOutcomeUnknownException>(()=>channel.SendAsync(new("client","Client","client@outside.test"),Request(),Guid.NewGuid()));
    }
    [Fact]
    public async Task ServerErrorAfterSendRequiresReconciliation()
    {
        var handler=new Handler{Status=HttpStatusCode.BadGateway};
        await Assert.ThrowsAsync<ExternalDeliveryOutcomeUnknownException>(()=>Channel(handler).SendAsync(new("client","Client","client@outside.test"),Request(),Guid.NewGuid()));
    }
    [Fact]
    public async Task PermissionRejectionRetainsHttpStatusForControlledHandling()
    {
        var handler=new Handler{Status=HttpStatusCode.Forbidden};
        var error=await Assert.ThrowsAsync<HttpRequestException>(()=>Channel(handler).SendAsync(new("client","Client","client@outside.test"),Request(),Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Forbidden,error.StatusCode);
    }
    [Fact]
    public async Task RoutedMailUsesTheAuthorizedMailboxAndMimeContentType()
    {
        var handler=new Handler();await Channel(handler).SendAsync(new("client","Client","client@outside.test"),Request(),Guid.NewGuid());
        Assert.EndsWith("/users/router%40studio.test/sendMail",handler.SendUrl);Assert.Equal("text/plain",handler.ContentType);
        string mime=Encoding.UTF8.GetString(Convert.FromBase64String(handler.Payload!));Assert.Contains("From: paul@studio.test",mime);
    }
    private static MicrosoftGraphEmailMessageChannel Channel(Handler handler)=>new(new HttpClient(handler),new(){TenantId="tenant",ClientId="client",ClientSecret="test",SenderUpn="router@studio.test",SkipSenderRecipient=false});
    private sealed class Handler:HttpMessageHandler
    {
        public bool TimeoutOnSend {get;init;}public HttpStatusCode Status {get;init;}=HttpStatusCode.Accepted;
        public string? SendUrl {get;private set;}public string? ContentType {get;private set;}public string? Payload {get;private set;}
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            if(request.RequestUri!.Host=="login.microsoftonline.com")return new(HttpStatusCode.OK){Content=new StringContent("{\"access_token\":\"test-token\"}")};
            SendUrl=request.RequestUri.AbsoluteUri;ContentType=request.Content!.Headers.ContentType?.MediaType;Payload=await request.Content.ReadAsStringAsync(token);
            if(TimeoutOnSend)throw new TaskCanceledException("Simulated lost provider response");
            return new(Status){Content=new StringContent("{}")};
        }
    }
}
