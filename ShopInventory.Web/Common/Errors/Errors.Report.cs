using ErrorOr;

namespace ShopInventory.Web.Common.Errors;

public static partial class Errors
{
    public static class Report
    {
        public static Error LoadMerchandiserPurchaseOrdersFailed(string message) =>
            Error.Failure("Report.LoadMerchandiserPurchaseOrdersFailed", message);
    }
}