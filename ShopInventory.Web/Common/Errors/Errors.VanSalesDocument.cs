using ErrorOr;

namespace ShopInventory.Web.Common.Errors;

public static partial class Errors
{
    public static class VanSalesDocument
    {
        public static Error LoadFailed(string message) =>
            Error.Failure("VanSalesDocument.LoadFailed", message);

        public static Error NotFound(string reference) =>
            Error.NotFound("VanSalesDocument.NotFound", $"No invoice from the van sales app has the reference {reference}.");

        /// <summary>The API refused or failed a post to SAP; the message is its own sentence.</summary>
        public static Error PostFailed(string message) =>
            Error.Failure("VanSalesDocument.PostFailed", message);

        /// <summary>The API refused or failed a fiscalisation retry; the message is its own sentence.</summary>
        public static Error FiscaliseFailed(string message) =>
            Error.Failure("VanSalesDocument.FiscaliseFailed", message);
    }
}
