using ErrorOr;

namespace ShopInventory.Web.Common.Errors;

public static partial class Errors
{
    public static class PurchaseInvoice
    {
        public static Error LoadInvoicesFailed(string message) =>
            Error.Failure("PurchaseInvoice.LoadInvoicesFailed", message);

        public static Error LoadInvoiceFailed(string message) =>
            Error.Failure("PurchaseInvoice.LoadInvoiceFailed", message);

        public static Error CreateInvoiceFailed(string message) =>
            Error.Failure("PurchaseInvoice.CreateInvoiceFailed", message);
    }
}