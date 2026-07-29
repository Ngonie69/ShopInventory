using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class WhatsApp
    {
        public static readonly Error Disabled =
            Error.Validation("WhatsApp.Disabled", "WhatsApp integration is disabled");

        public static readonly Error InvalidPayload =
            Error.Validation("WhatsApp.InvalidPayload", "The inbound WhatsApp webhook payload is empty");

        public static readonly Error InvalidWebhookSignature =
            Error.Unauthorized("WhatsApp.InvalidWebhookSignature", "OpenWA webhook authentication failed");

        public static readonly Error MissingWebhookSecretConfiguration =
            Error.Validation("WhatsApp.MissingWebhookSecretConfiguration", "OpenWA:WebhookSecret must be configured before accepting inbound webhooks");

        public static Error InvalidConfiguration(string message) =>
            Error.Validation("WhatsApp.InvalidConfiguration", message);

        public static Error Unreachable(string message) =>
            Error.Failure("WhatsApp.Unreachable", message);
    }
}