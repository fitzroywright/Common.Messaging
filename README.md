# Common.Messaging

Reusable messaging and notification library for the application family.

## Repository policy

`main` is the authoritative trunk and the only branch that should be used for ongoing development, integration, packaging, and releases. Older development, hardening, and operations branches are historical once their work has been incorporated into `main`.

## Application user integration

Common.Messaging does **not** reference any application's user entity. Applications implement `IMessageRecipientDirectory` and map their own user model into the neutral `MessageRecipient` snapshot.

That allows Studio, Cafeteria, RequestPortal, SensorNetwork, and future applications to keep their own user/domain persistence while sharing one messaging engine. External channel implementations consume `MessageRecipient`, never an application-specific user entity directly.

## Delivery models

`MessageService` remains the simple synchronous implementation for lightweight scenarios and backward compatibility.

`QueuedMessageService` is the production-oriented path. It persists in-app notifications immediately, snapshots recipients, queues external delivery, and can signal connected clients through `IMessageSignalSender`.

Queued external delivery uses the external delivery queue/processor/dispatcher contracts and supports durable implementations in addition to the bounded in-memory queue.

`ExternalDeliveryDispatcher` supports `Sequential`, `Parallel`, and `ParallelByRecipient` modes with configurable maximum concurrency. Failures are isolated per recipient/channel and can be recorded through `IExternalDeliveryFailureSink` instead of aborting unrelated deliveries.

The in-memory queue is bounded and waits when full, providing backpressure instead of allowing unbounded memory growth.

Applications may supply optional `ContextId` / `ContextName` values when constructing `QueuedMessageService`. These provide neutral workspace/application context without coupling Common.Messaging to a particular application framework.

## Durable delivery

Common.Messaging includes file-backed and PostgreSQL-backed durable external-delivery infrastructure, durable idempotency/delivery receipts, dead-letter handling, maintenance, and queue-health support.

The PostgreSQL implementation serializes schema initialization so simultaneous startup from multiple application nodes does not race creation of shared queue structures. Multi-node integration tests exercise the durable PostgreSQL path.

## Hosted worker integration

The separate `Common.Messaging.Hosting` project supplies standard .NET background-service integration without making the core library depend directly on Microsoft.Extensions.Hosting.

Call `AddCommonMessagingQueuedDelivery(...)` after registering your `IMessageRecipientDirectory`, `IMessageStore`, external channels, and optional signal/failure handlers. The hosting integration registers the queue, dispatcher, queue processor, hosted worker, and `QueuedMessageService` as `IMessageService`.

## Dead-letter administration

Durable providers expose `IExternalDeliveryDeadLetterStore` for operational administration. Dead letters can be inspected, replayed individually, replayed in bulk, discarded individually, or discarded in bulk. Discard is explicit and irreversible; normal delivery processing never silently removes dead letters.

## Retention maintenance

Retention cleanup is explicit and opt-in through `IExternalDeliveryMaintenance`. Nothing is automatically pruned merely because the library is registered.

File and PostgreSQL maintenance implementations clean eligible completed/idempotency records according to configured retention. Delivery receipts are the durable idempotency record, so receipt retention must be chosen deliberately based on the application's retry/replay and audit requirements.

Dead letters are not silently removed by ordinary retention maintenance. They remain until replayed or explicitly discarded through the dead-letter administration contract.

## Real-time signaling

`IMessageSignalSender` is an optional hook used after messages are accepted. Applications can implement it with SignalR, WebSockets, desktop eventing, or another transport so inboxes refresh without polling.

## Channels

The messaging model supports in-app and external notification channels. Concrete integrations on `main` include SMTP, Slack, Microsoft Teams, SMS, Graph email, and Meta WhatsApp Cloud API. WhatsApp is provider-neutral at the application boundary and uses Meta's Graph API from the shared channel implementation.

A queued dispatcher sends only when both the requested channels and the recipient's preferred channels permit the channel. This preserves recipient notification preferences.

## Secrets

Common.Messaging core does not own secret storage. SMTP, Slack, Teams, SMS, Graph, WhatsApp, and other adapters should obtain credentials through `Common.Secrets` at the host/integration boundary and pass resolved configuration to the channel implementation. This keeps the messaging core testable and independent of a specific vault.

## Dependency rule

Common.Messaging does not directly reference Common.Secrets or Common.Security. Applications compose those libraries at their integration boundary. This avoids dependency cycles and keeps the Common cores independently reusable.

## Current maturity

The messaging engine, queued delivery, durable file/PostgreSQL stores, idempotency, dead-letter administration, retention maintenance, channel implementations, hosting integration, and automated tests are implemented on `main`. Remaining work is primarily operational proof in real consumers: exercise actual external endpoints/credentials, force delivery failures, verify retry/dead-letter/replay behavior, and validate the PostgreSQL durable queue in the intended deployment environment.


### Microsoft Graph report emails

`MessageRequest.Attachments` accepts `MessageAttachment(FileName, ContentType, Content)`
for the Microsoft Graph email channel. `BodyIsHtml` defaults to `true`, preserving
existing Graph callers; set it to `false` for plain-text reports. The Graph provider
supports up to 2.5 MB combined raw attachment bytes and a 4 MB serialized payload.
Oversized messages fail explicitly before an HTTP request; large-attachment upload
sessions are not implemented. Other external channels do not consume this new
attachment property.

`MicrosoftGraphEmailOptions.SkipSenderRecipient` defaults to `true` for compatibility.
Applications that need to send reports or Test Email to their own sender mailbox
can set it to `false`. Successful `sendMail` means provider acceptance, not confirmed
mailbox delivery. Applications with an existing durable queue can call the channel
from that queue and retain their own retry and correlation records.


## Meta WhatsApp Cloud API

The shared WhatsApp channel sends through Meta's WhatsApp Cloud API at `graph.facebook.com`. Hosts configure the Graph API version, WhatsApp Business phone-number ID, and a Common.Secrets-backed access token (`messaging/whatsapp/access-token` by default). Applications provide a recipient mobile number in international format. Message metadata may optionally specify an approved WhatsApp template name, language, and pipe-delimited body parameters for template delivery.
