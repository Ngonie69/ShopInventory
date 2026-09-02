using ErrorOr;

namespace ShopInventory.Web.Common.Errors;

public static partial class Errors
{
    public static class CreditNoteApproval
    {
        public static Error LoadFailed(string message) =>
            Error.Failure("CreditNoteApproval.LoadFailed", message);

        public static Error DecisionFailed(string message) =>
            Error.Failure("CreditNoteApproval.DecisionFailed", message);

        public static Error AddFailed(string message) =>
            Error.Failure("CreditNoteApproval.AddFailed", message);
    }
}
