# Common.Messaging

Reusable messaging and notification library for Aegis applications.

## ApplicationUser integration

Common.Messaging does **not** reference any application's `ApplicationUser` class. Instead, applications implement `IMessageRecipientDirectory` and map their own user model into the neutral `MessageRecipient` snapshot.

That allows Aegis NGO, Aegis Studio, Cafeteria, and future applications to keep their own user persistence while sharing one messaging engine.

Example adapter:

```csharp
public sealed class StudioRecipientDirectory : IMessageRecipientDirectory
{
    public Task<IReadOnlyList<MessageRecipient>> ResolveAsync(
        IReadOnlyCollection<string> recipientIds,
        CancellationToken cancellationToken = default)
    {
        // Look up Studio users / AD-backed profiles here and return MessageRecipient values.
        throw new NotImplementedException();
    }
}
```

External channel implementations consume `MessageRecipient`, never `ApplicationUser` directly.

## Secrets

Common.Messaging core does not own secret storage. Slack, Graph, SMTP, WhatsApp, and other channel adapters should obtain credentials through `Common.Secrets` in the host/integration layer and pass the resolved configuration to the channel implementation. This keeps the messaging core testable and independent of any specific vault.
