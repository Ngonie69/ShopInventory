using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class CreditNote
    {
        public static Error NotFound(int id) =>
            Error.NotFound("CreditNote.NotFound", $"Credit note with ID {id} not found.");

        public static Error NotFoundByNumber(string creditNoteNumber) =>
            Error.NotFound("CreditNote.NotFound", $"Credit note '{creditNoteNumber}' not found.");

        public static readonly Error Unauthorized =
            Error.Unauthorized("CreditNote.Unauthorized", "User is not authenticated.");

        public static Error InvalidOperation(string message) =>
            Error.Validation("CreditNote.InvalidOperation", message);

        public static Error CreationFailed(string message) =>
            Error.Failure("CreditNote.CreationFailed", message);
    }
}
