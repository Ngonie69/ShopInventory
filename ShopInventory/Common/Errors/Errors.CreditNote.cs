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

        /// <summary>
        /// SAP answered the post and refused it, so no credit note exists.
        /// </summary>
        /// <remarks>
        /// Kept apart from <see cref="CreationFailed"/>, which now also covers failures that leave
        /// the document's existence unknown. This one says plainly that nothing was created and the
        /// same request may be corrected and sent again.
        /// </remarks>
        public static Error SapRejected(string message) =>
            Error.Validation("CreditNote.SapRejected", $"SAP refused the credit note: {message}");

        public static Error BulkCancellationFailed(string message) =>
            Error.Failure("CreditNote.BulkCancellationFailed", message);

        public static Error DuplicationFailed(string message) =>
            Error.Failure("CreditNote.DuplicationFailed", message);

        public static Error ReasonsUnavailable(string message) =>
            Error.Failure("CreditNote.ReasonsUnavailable", $"Unable to read the credit note reasons from SAP. {message}");
    }
}
