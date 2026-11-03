using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class DailySalesWhatsApp
    {
        public static readonly Error NoRecipients =
            Error.Validation("DailySalesWhatsApp.NoRecipients", "Name at least one WhatsApp number to send the daily sales report to.");

        public static Error InvalidNumber(string number) =>
            Error.Validation("DailySalesWhatsApp.InvalidNumber", $"'{number}' is not a phone number WhatsApp can send to.");

        public static Error SendingNumber(string number) =>
            Error.Validation(
                "DailySalesWhatsApp.SendingNumber",
                $"{number} is the number WhatsApp messages are sent from, so it cannot receive the report.");

        public static Error UnknownShop(string warehouse) =>
            Error.Validation("DailySalesWhatsApp.UnknownShop", $"No shop sells from warehouse '{warehouse}'.");

        public static readonly Error TooLong =
            Error.Validation(
                "DailySalesWhatsApp.TooLong",
                "The rows are too long to save. Shorten the labels or remove some rows.");

        public static readonly Error GatewayNotConfigured =
            Error.Failure(
                "DailySalesWhatsApp.GatewayNotConfigured",
                "WhatsApp is not set up on this server, so the report cannot be sent from it.");

        public static Error NoSendingNumber(string detail) =>
            Error.Failure("DailySalesWhatsApp.NoSendingNumber", detail);

        public static readonly Error FutureDay =
            Error.Validation("DailySalesWhatsApp.FutureDay", "The report can only be sent for today or a day already past.");
    }
}
