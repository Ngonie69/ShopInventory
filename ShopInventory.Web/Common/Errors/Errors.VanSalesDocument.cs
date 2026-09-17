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
    }
}
