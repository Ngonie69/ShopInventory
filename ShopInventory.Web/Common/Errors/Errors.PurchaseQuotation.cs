using ErrorOr;

namespace ShopInventory.Web.Common.Errors;

public static partial class Errors
{
    public static class PurchaseQuotation
    {
        public static Error LoadQuotationsFailed(string message) =>
            Error.Failure("PurchaseQuotation.LoadQuotationsFailed", message);

        public static Error CreateQuotationFailed(string message) =>
            Error.Failure("PurchaseQuotation.CreateQuotationFailed", message);
    }
}