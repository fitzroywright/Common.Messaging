# Common.Messaging

Reusable messaging and notification library for Aegis applications.

## ApplicationUser integration

Common.Messaging does **not** reference any application's `ApplicationUser` class. Applications implement `IMessageRecipientDirectory` and map their own user model into the neutral `MessageRecipient` snapshot.

That allows Aegis NGO, Aegis Studio, Cafeteria, RequestPortal, and future applications to keep their own user persistence while sharing one messaging engine.

External channel implementations consume `MessageRecipient`, never an application-specific user entity directly.

## Delivery models

`MessageService` remains the simple synchronous implementation for lightweight scenarios and backward compatibility.

`QueuedMessageService` is the production-oriented path. It persists in-app notifications immediately, snapshots recipients, queues external delivery, and can signal connected clients through `IMessageSignalSender`.

Queued external delivery uses:

- `IExternalDeliveryQueue`
- `InMemoryExternalDeliveryQueue`
- `ExternalDeliveryWorkItem`
- `RecipientSnapshot`
- `ExternalDeliveryProcessor`
- `IExternalDeliveryDispatcher`
- `ExternalDeliveryDispatcher`
- `IExternalDeliveryFailureSink`

`ExternalDeliveryDispatcher` supports `Sequential`, `Parallel`, and `ParallelByRecipient` modes with configurable maximum concurrency. Failures are isolated per recipient/channel and can be recorded through `IExternalDeliveryFailureSink` instead of aborting unrelated deliveries.

The in-memory queue is bounded and waits when full, providing backpressure instead of allowing unbounded memory growth.

Applications may supply optional `ContextId` / `ContextName` values when constructing `QueuedMessageService`. These provide neutral tenant/workspace/application context without coupling Common.Messaging to DevExpress or any specific tenancy system.

## Real-time signaling

`IMessageSignalSender` is an optional hook used after messages are accepted. Applications can implement it with SignalR, WebSockets, desktop eventing, or another transport so inboxes refresh without polling.

## Channels

Core channel flags currently include InApp, SMTP, Microsoft Teams, Microsoft Graph email, Slack, WhatsApp, and AudioAlert. Applications or integration projects provide the concrete `IExternalMessageChannel` implementations.

A queued dispatcher sends only when both the requested channels and the recipient's preferred channels permit the channel. This preserves recipient notification preferences.

## Secrets

Common.Messaging core does not own secret storage. Slack, Graph, SMTP, WhatsApp, and other channel adapters should obtain credentials through `Common.Secrets` in the host/integration layer and pass resolved configuration to the channel implementation. This keeps the messaging core testable and independent of any specific vault.

## Dependency rule

Common.Messaging does not directly reference Common.Secrets or Common.Security. Applications compose those libraries at their integration boundary. This avoids dependency cycles and keeps the three Common cores independently reusable.
