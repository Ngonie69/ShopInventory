using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class DesktopSalesReview
    {
        public static readonly Error NoRecipients =
            Error.Validation("DesktopSalesReview.NoRecipients", "Name at least one email address to send the review to.");

        public static readonly Error NotScheduled =
            Error.Validation(
                "DesktopSalesReview.NotScheduled",
                "The review email has not been set up. An admin saves the schedule in Settings, and the review is read as that admin.");

        public static Error UnknownCadence(string cadence) =>
            Error.Validation("DesktopSalesReview.UnknownCadence", $"'{cadence}' is not a review period. Use weekly, monthly or custom.");

        public static readonly Error EmailDisabled =
            Error.Failure("DesktopSalesReview.EmailDisabled", "Email is switched off in the API's configuration, so the review could not be sent.");
    }
}
