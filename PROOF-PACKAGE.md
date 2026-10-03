# Common.Messaging proof package

## Existing proof coverage

The repository already contains automated tests for the message service, queued delivery, durable queues/idempotency, PostgreSQL multi-node behavior, dead letters, maintenance, provider payload construction, and delivery-state semantics.

The Microsoft Graph email adapter already distinguishes provider acceptance from confirmed delivery through `MessageDeliveryState`.

## Real Microsoft 365 proof

`Common.Messaging.UnitTests/MicrosoftGraphRealProviderProofTests.cs` performs a controlled real-provider test.

Set:

- `COMMON_MESSAGING_M365_TENANT_ID`
- `COMMON_MESSAGING_M365_CLIENT_ID`
- `COMMON_MESSAGING_M365_CLIENT_SECRET`
- `COMMON_MESSAGING_M365_SENDER_UPN`
- `COMMON_MESSAGING_M365_TEST_RECIPIENT`

Then run:

`dotnet test Common.Messaging.UnitTests/Common.Messaging.UnitTests.csproj --filter Microsoft365_RealProviderSend_IsAcceptedByGraph`

The test:

1. acquires a real application token;
2. verifies that the token includes the `Mail.Send` application role;
3. sends to the designated diagnostic recipient;
4. includes a small attachment;
5. treats successful `sendMail` completion as **ProviderAccepted**, not `Delivered`.

When configuration is absent the test is reported as skipped with the exact missing requirements.

## Diagnostic coverage already present

`MicrosoftGraphEmailMessageChannel` implements `IExternalMessageChannelDiagnostic` and verifies:

- required provider configuration;
- secret resolution;
- Microsoft Entra token acquisition;
- presence of the `Mail.Send` application role;
- provider reachability/readiness state.

No secret value is included in the returned diagnostic health model.

## Architectural blocker to the requested canonical messaging proof

The current Common.Messaging public model is a notification/delivery abstraction:

- `MessageRequest`
- `MessageNotification`
- `IMessageService`
- `IMessageStore`
- external delivery queues/channels.

It does **not** currently define a provider-neutral canonical conversation/thread/message model with reply continuity. Therefore the comprehensive requirements for:

- create canonical message;
- receive/process into a canonical conversation;
- reply tied to original conversation;
- conversation/thread continuity;
- attachment-to-message/conversation persistence

cannot honestly be certified at the Common.Messaging layer yet.

Those behaviors may exist in consuming applications, but application-specific conversation persistence is not equivalent to proving Common.Messaging itself. The next architecture change should be additive: introduce canonical conversation/message contracts and persistence behind Common.Messaging without embedding Request Portal, Studio, or FFP Manager rules in the common library.


## Current proof status (2026-10-02)

Completed with the real Microsoft 365 provider:

- baseline Common.Messaging suite green;
- real Microsoft Graph application token acquisition passed;
- real Graph sendMail proof passed;
- attachment send passed;
- recipient mailbox receipt was manually observed;
- intentionally invalid client secret failed cleanly at token acquisition;
- restoring the valid secret returned the same proof to a passing state.

Operational discovery during proof:

- the Request Portal Microsoft Graph application client secret had expired;
- the secret was rotated and the updated value restored successful token acquisition and mail sending.

Intentionally deferred:

- valid identity without `Mail.Send` authorization;
- other authorization-specific provider failures;
- broader network interruption fault injection.

These deferred authorization tests are not counted as passed. Successful Graph acceptance remains distinct from confirmed recipient delivery; this proof has both provider acceptance and one manually observed recipient delivery.
