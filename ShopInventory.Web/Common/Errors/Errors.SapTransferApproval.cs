using ErrorOr;

namespace ShopInventory.Web.Common.Errors;

public static partial class Errors
{
    public static class SapTransferApproval
    {
        public static Error LoadFailed(string message) =>
            Error.Failure("SapTransferApproval.LoadFailed", message);

        public static Error AddFailed(string message) =>
            Error.Failure("SapTransferApproval.AddFailed", message);

        public static Error DecideFailed(string message) =>
            Error.Failure("SapTransferApproval.DecideFailed", message);
    }
}
