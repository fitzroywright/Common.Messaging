# Routed email additions — October 11, 2026

Common.Messaging retains its existing transport interfaces and JSON sendMail behavior for callers without Email.MessageId metadata. Routed callers opt into a MIME envelope with stable Message-ID, authorized From/FromName, monitored Reply-To, In-Reply-To/References and small attachments. SenderUpn selects the actual mailbox endpoint; the From identity must have verified tenant Send As authority.

IEmailRecipientDirectory provides provider-independent membership discovery. The Microsoft Graph implementation retrieves supported static mail-enabled group members, follows only trusted Graph pagination, caches results, and explicitly holds unsupported/incomplete/hidden/nested/dynamic groups. It does not provision or duplicate group memberships. The Studio router decides whether a group is registered, verified and eligible for selective fan-out.

ExternalDeliveryOutcomeUnknownException distinguishes a potentially accepted provider send from a known rejection. A timeout, lost network response or server failure must not trigger a blind replay. Graph sendMail acceptance is not confirmed recipient delivery. The mailbox reader exposes the stable RFC InternetMessageId and original provider message ID so callers can correlate replies and preserve compatibility with older stored Graph IDs.

New tests cover the MIME envelope, unknown outcomes, authorization errors, static group pagination/cache and unsupported groups. They have not been run. Build/test locally with the repository's existing workflow. Verify Send As, MIME behavior, discovery permissions and original-recipient headers against the actual Microsoft 365 tenant before enabling Studio fan-out. Large attachment upload sessions and Google Workspace are deferred.
