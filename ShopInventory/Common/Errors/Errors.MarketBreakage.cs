using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class MarketBreakage
    {
        public static readonly Error SapDisabled =
            Error.Failure("MarketBreakage.SapDisabled", "SAP integration is disabled, so nothing can be transferred to returns.");

        public static Error NotFound(int id) =>
            Error.NotFound("MarketBreakage.NotFound", $"Breakage report {id} was not found.");

        public static readonly Error UserNotFound =
            Error.NotFound("MarketBreakage.UserNotFound", "Your account could not be found.");

        public static readonly Error NoVanWarehouse =
            Error.Validation("MarketBreakage.NoVanWarehouse",
                "Your account has no van warehouse assigned, so the breakage cannot be recorded against a van. Ask the office to assign one.");

        public static Error NotActionable(string status) =>
            Error.Conflict("MarketBreakage.NotActionable", $"This breakage report is {status} and can no longer be confirmed or rejected.");

        public static readonly Error DuplicateRequestFromAnotherUser =
            Error.Conflict("MarketBreakage.DuplicateRequest", "This report id has already been used by another account.");

        public static Error LinesMismatch(string message) =>
            Error.Validation("MarketBreakage.LinesMismatch", message);

        public static readonly Error NothingToTransfer =
            Error.Validation("MarketBreakage.NothingToTransfer",
                "Every confirmed quantity is zero, so there is nothing to transfer. Reject the report instead.");

        public static readonly Error PostInProgress =
            Error.Conflict("MarketBreakage.PostInProgress",
                "This breakage is already being transferred to returns. Wait for it to finish, then refresh.");

        public static Error InsufficientStock(string message) =>
            Error.Validation("MarketBreakage.InsufficientStock", message);

        public static Error TransferFailed(string message) =>
            Error.Failure("MarketBreakage.TransferFailed", message);
    }
}
