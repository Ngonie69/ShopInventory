using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    /// <summary>Refusals when a vending cash-up is counted, approved or queried.</summary>
    public static class VendingCashUp
    {
        public static Error NotFound =>
            Error.NotFound("VendingCashUp.NotFound", "That cash-up no longer exists.");

        public static Error CashierNotFound =>
            Error.NotFound("VendingCashUp.CashierNotFound", "That cashier is not a vending account.");

        /// <summary>A count needs a person behind it, so it can be told who may approve it.</summary>
        public static Error SignedInUserRequired =>
            Error.Forbidden(
                "VendingCashUp.SignedInUserRequired",
                "A cash-up has to be keyed and decided by a signed-in user, not by the API key on its own.");

        public static Error NotAllowedToCount =>
            Error.Forbidden("VendingCashUp.NotAllowedToCount", "Only an Admin or a Cashier can key a cash-up.");

        public static Error ApproverMustBeAdmin =>
            Error.Forbidden("VendingCashUp.ApproverMustBeAdmin", "Only an Admin can approve or query a cash-up.");

        /// <summary>The control a cash-up exists for: the person who counted it does not sign it off.</summary>
        public static Error CannotApproveOwnCount =>
            Error.Forbidden(
                "VendingCashUp.CannotApproveOwnCount",
                "You keyed or edited this count, so another Admin has to approve it.");

        public static Error WarehouseNotTheCashiers(string warehouse) =>
            Error.Validation(
                "VendingCashUp.WarehouseNotTheCashiers",
                $"{warehouse} is not one of this cashier's warehouses, and they sold nothing from it that day.");

        public static Error DateInFuture =>
            Error.Validation("VendingCashUp.DateInFuture", "A day that has not happened yet cannot be cashed up.");

        public static Error DenominationsDisagree(string currency, decimal fromNotes, decimal typed) =>
            Error.Validation(
                "VendingCashUp.DenominationsDisagree",
                $"The {Currency(currency)} notes and coins add up to {fromNotes:N2}, but {typed:N2} was entered as the count.");

        public static Error CountRequired(string currency) =>
            Error.Validation(
                "VendingCashUp.CountRequired",
                $"Enter the {Currency(currency)} counted, or the notes and coins it is made of.");

        public static Error BankedMoreThanCounted(string currency) =>
            Error.Validation(
                "VendingCashUp.BankedMoreThanCounted",
                $"More {Currency(currency)} was banked than was counted.");

        public static Error AlreadyApproved =>
            Error.Conflict("VendingCashUp.AlreadyApproved", "This cash-up has been approved and can no longer change.");

        public static Error NotAwaitingApproval(string status) =>
            Error.Conflict(
                "VendingCashUp.NotAwaitingApproval",
                status == "Queried"
                    ? "This cash-up was queried. It can be approved once the count has been saved again with an answer."
                    : $"This cash-up is {status.ToLowerInvariant()}, not waiting for approval.");

        /// <summary>The page was read before someone else saved or decided the same count.</summary>
        public static Error Stale =>
            Error.Conflict(
                "VendingCashUp.Stale",
                "Someone else changed this cash-up since it was opened. Reload it and try again.");

        /// <summary>
        /// A sale landed after the count (an offline till catching up, usually), so the variance the
        /// approver would sign is not the one the counter saw.
        /// </summary>
        public static Error SalesChangedSinceCount =>
            Error.Conflict(
                "VendingCashUp.SalesChangedSinceCount",
                "Sales for this cashier and day have changed since the count was saved. Open the count and save it again against the current sales.");

        public static Error ReasonRequired =>
            Error.Validation(
                "VendingCashUp.ReasonRequired",
                "This count does not agree with the sales. Record why on the count before it is approved.");

        public static Error QueryNoteRequired =>
            Error.Validation("VendingCashUp.QueryNoteRequired", "Say what needs answering before sending the count back.");

        private static string Currency(string code) => code == "ZIG" ? "ZiG" : code;
    }
}
