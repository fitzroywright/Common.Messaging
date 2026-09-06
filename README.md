# Common.Messaging

Reusable application messaging and notification primitives for Aegis applications.

Common.Messaging deliberately does not depend on an application's `ApplicationUser` type. Applications expose recipients through `IMessageRecipientDirectory`, allowing each application to adapt its own user model without coupling the shared library to it.
