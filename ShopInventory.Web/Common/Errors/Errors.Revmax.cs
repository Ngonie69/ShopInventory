using ErrorOr;

namespace ShopInventory.Web.Common.Errors;

public static partial class Errors
{
    public static class Revmax
    {
        public static Error LoadDraftFailed(string message) =>
            Error.Failure("Revmax.LoadDraftFailed", message);

        public static Error CreditNoteNotFound(string creditNoteNumber) =>
            Error.NotFound("Revmax.CreditNoteNotFound", $"Credit note {creditNoteNumber} was not found.");

        public static Error FiscalizationFailed(string message) =>
            Error.Failure("Revmax.FiscalizationFailed", message);
    }
}