using ErrorOr;
using ShopInventory.Features.Maintenance;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class Maintenance
    {
        /// <summary>
        /// What a phone is told when the lockout refuses it.
        /// </summary>
        /// <remarks>
        /// The description is the fallback wording only. The middleware sends the operator's own
        /// message when there is one, because "the system is down until 03:00 for the stock
        /// migration" is worth more to somebody standing in front of a customer than any wording
        /// that can be baked in here.
        /// </remarks>
        public static readonly Error MobileTransactionsSuspended =
            Error.Failure(
                "Maintenance.MobileTransactionsSuspended",
                MaintenanceState.DefaultMessage);

        public static Error UpdateFailed(string message) =>
            Error.Failure("Maintenance.UpdateFailed", message);

        public static Error UnsupportedApp(string appId) =>
            Error.Validation("Maintenance.UnsupportedApp", $"Unknown mobile app: {appId}");

        public static readonly Error EndsInThePast =
            Error.Validation(
                "Maintenance.EndsInThePast",
                "The maintenance window ends in the past, so the lockout would not apply to anything.");
    }
}
