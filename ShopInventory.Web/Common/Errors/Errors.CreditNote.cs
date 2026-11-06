using ErrorOr;

namespace ShopInventory.Web.Common.Errors;

public static partial class Errors
{
    public static class CreditNote
    {
        public static Error LoadFailed(string message) =>
            Error.Failure("CreditNote.LoadFailed", message);

        public static Error CreateFailed(string message) =>
            Error.Failure("CreditNote.CreateFailed", message);

        public static Error UpdateFailed(string message) =>
            Error.Failure("CreditNote.UpdateFailed", message);

        public static Error BulkCancelFailed(string message) =>
            Error.Failure("CreditNote.BulkCancelFailed", message);

        public static Error DuplicateFailed(string message) =>
            Error.Failure("CreditNote.DuplicateFailed", message);

        /// <summary>The account named is not one this portal login reaches.</summary>
        public static readonly Error NotYourAccount =
            Error.Forbidden("CreditNote.NotYourAccount", "That credit note is not on your account.");

        public static Error PdfFailed(string message) =>
            Error.Failure("CreditNote.PdfFailed", message);
    }
}