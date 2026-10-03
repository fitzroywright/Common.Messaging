using Common.Messaging.Channels.MicrosoftGraph;
using Xunit;

namespace Common.Messaging.UnitTests;

public sealed class MicrosoftGraphRealProviderProofTests
{
    [ExternalProviderFact(
        "COMMON_MESSAGING_M365_TENANT_ID,COMMON_MESSAGING_M365_CLIENT_ID,COMMON_MESSAGING_M365_CLIENT_SECRET,COMMON_MESSAGING_M365_SENDER_UPN,COMMON_MESSAGING_M365_TEST_RECIPIENT",
        "Configure a dedicated Microsoft 365 diagnostic recipient before running this proof.")]
    public async Task Microsoft365_RealProviderSend_IsAcceptedByGraph()
    {
        string? tenantId = Environment.GetEnvironmentVariable("COMMON_MESSAGING_M365_TENANT_ID");
        string? clientId = Environment.GetEnvironmentVariable("COMMON_MESSAGING_M365_CLIENT_ID");
        string? clientSecret = Environment.GetEnvironmentVariable("COMMON_MESSAGING_M365_CLIENT_SECRET");
        string? sender = Environment.GetEnvironmentVariable("COMMON_MESSAGING_M365_SENDER_UPN");
        string? recipient = Environment.GetEnvironmentVariable("COMMON_MESSAGING_M365_TEST_RECIPIENT");

        MicrosoftGraphEmailMessageChannel channel = new(
            new HttpClient(),
            new MicrosoftGraphEmailOptions
            {
                TenantId = tenantId,
                ClientId = clientId,
                ClientSecret = clientSecret,
                SenderUpn = sender,
                SkipSenderRecipient = false
            });

        MessagingProviderHealth health = await channel.VerifyProviderAsync();
        Assert.True(health.Configured, health.Reason);
        Assert.True(health.Reachable, health.Reason);
        Assert.Equal(MessageDeliveryState.ProviderAccepted, health.State);

        byte[] attachment = System.Text.Encoding.UTF8.GetBytes("Common.Messaging real-provider proof");
        MessageRequest request = new()
        {
            Title = $"Common.Messaging proof {DateTimeOffset.UtcNow:O}",
            Body = "This is a controlled Common.Messaging Microsoft 365 integration proof.",
            BodyIsHtml = false,
            Channels = MessageChannel.MsEmail,
            CorrelationId = Guid.NewGuid().ToString("N"),
            Attachments =
            [
                new MessageAttachment("common-messaging-proof.txt", "text/plain", attachment)
            ]
        };

        await channel.SendAsync(
            new MessageRecipient("proof-recipient", "Proof Recipient", recipient, PreferredChannels: MessageChannel.MsEmail),
            request,
            Guid.NewGuid());

        // Graph sendMail returning success proves provider acceptance for sending.
        // It does not prove mailbox receipt/delivery; the distinction is intentional.
        Assert.Equal(
            MessageDeliveryState.ProviderAccepted,
            MessagingDeliverySemantics.ProviderSendCompleted(providerSupportsDeliveryReceipts: false));
    }
}
