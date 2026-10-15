using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class SalesPerformanceEmail
    {
        public static readonly Error NoRecipients =
            Error.Validation("SalesPerformanceEmail.NoRecipients", "Name at least one email address to send the sales performance email to.");

        public static readonly Error NotScheduled =
            Error.Validation(
                "SalesPerformanceEmail.NotScheduled",
                "The sales performance email has not been set up. An admin saves its schedule in Settings, and the shop and vending figures are read as that admin.");

        public static Error UnknownCadence(string cadence) =>
            Error.Validation("SalesPerformanceEmail.UnknownCadence", $"'{cadence}' is not a period. Use daily, weekly, monthly or custom.");

        public static readonly Error EmailDisabled =
            Error.Failure("SalesPerformanceEmail.EmailDisabled", "No recipient accepted the email. Email may be switched off in the API's configuration.");
    }
}
