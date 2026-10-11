namespace Common.Messaging.UnitTests;

using System.Net;
using Common.Messaging.Channels.MicrosoftGraph;
using Xunit;

public sealed class MicrosoftGraphRecipientDirectoryTests
{
    private const string GroupId="11111111-1111-1111-1111-111111111111";
    [Fact]
    public async Task StaticGroupUsesAllPagesAndCachesCompleteMembership()
    {
        var handler=new Handler();var directory=Create(handler);
        var group=await directory.GetGroupAsync(GroupId);Assert.True(group.Supported);Assert.Equal(2,group.Members.Count);
        Assert.Contains("router@studio.test",group.Members);Assert.Contains("client@outside.test",group.Members);
        int requests=handler.Requests;await directory.GetGroupAsync(GroupId);Assert.Equal(requests,handler.Requests);
    }
    [Theory]
    [InlineData("dynamic")]
    [InlineData("hidden")]
    [InlineData("nested")]
    [InlineData("missing")]
    [InlineData("denied")]
    [InlineData("foreign-page")]
    public async Task UnsupportedOrIncompleteMembershipNeverReturnsAPartialRecipientSet(string behavior)
    {
        var group=await Create(new Handler{Behavior=behavior}).GetGroupAsync(GroupId);
        Assert.False(group.Supported);Assert.Empty(group.Members);Assert.False(string.IsNullOrWhiteSpace(group.AttentionReason));
    }
    [Fact]
    public async Task ProviderReferencesCannotRedirectCredentialsToAnArbitraryUrl()
    {
        var handler=new Handler();var result=await Create(handler).GetGroupAsync("https://outside.test/token");
        Assert.False(result.Supported);Assert.Equal(0,handler.Requests);
    }
    private static MicrosoftGraphRecipientDirectory Create(Handler handler)=>new(new HttpClient(handler),new(){TenantId="tenant",ClientId="client",ClientSecretName="test"},new Secrets());
    private sealed class Secrets:IChannelSecretResolver {public Task<string?> GetSecretAsync(string name,CancellationToken token=default)=>Task.FromResult<string?>("test-only-secret");}
    private sealed class Handler:HttpMessageHandler
    {
        public string Behavior {get;init;}="normal";public int Requests {get;private set;}
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            Requests++;var uri=request.RequestUri!;
            if(uri.Host=="login.microsoftonline.com")return Response("{\"access_token\":\"test-token\"}");
            Assert.Equal("graph.microsoft.com",uri.Host);
            if(!uri.AbsolutePath.EndsWith("/members"))return Response("{\"id\":\""+GroupId+"\",\"mail\":\"tamara@studio.test\",\"mailEnabled\":true,\"groupTypes\":"+(Behavior=="dynamic"?"[\"DynamicMembership\"]":"[]")+",\"visibility\":\""+(Behavior=="hidden"?"HiddenMembership":"Public")+"\"}");
            if(Behavior=="denied")return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            if(Behavior=="nested")return Response("{\"value\":[{\"@odata.type\":\"#microsoft.graph.group\",\"mail\":\"nested@studio.test\"}]}");
            if(Behavior=="missing")return Response("{\"value\":[{\"@odata.type\":\"#microsoft.graph.user\",\"id\":\"limited-access\"}]}");
            if(uri.Query.Contains("skiptoken"))return Response("{\"value\":[{\"@odata.type\":\"#microsoft.graph.orgContact\",\"mail\":\"client@outside.test\"}]}");
            string next=Behavior=="foreign-page"?"https://outside.test/secret":"https://graph.microsoft.com/v1.0/groups/"+GroupId+"/members?$skiptoken=next";
            return Response("{\"value\":[{\"@odata.type\":\"#microsoft.graph.user\",\"mail\":\"router@studio.test\"}],\"@odata.nextLink\":\""+next+"\"}");
        }
        private static Task<HttpResponseMessage> Response(string body)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(body)});
    }
}
