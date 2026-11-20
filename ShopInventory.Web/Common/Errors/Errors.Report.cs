using ErrorOr;

namespace ShopInventory.Web.Common.Errors;

public static partial class Errors
{
    public static class Report
    {
        public static Error LoadItemVolumeSalesFailed(string message) =>
            Error.Failure("Report.LoadItemVolumeSalesFailed", message);

        public static Error LoadMerchandiserPurchaseOrdersFailed(string message) =>
            Error.Failure("Report.LoadMerchandiserPurchaseOrdersFailed", message);

        public static Error LoadDesktopSalesAnalysisFailed(string message) =>
            Error.Failure("Report.LoadDesktopSalesAnalysisFailed", message);

        /// <summary>
        /// The API's 404 for a ShopStock account with no sales partner set: not a failure, a state that
        /// /shop-sales draws in words. Keyed by its own code so the page can tell it from a real failure.
        /// </summary>
        public static Error SalesPartnerNotAssigned(string message) =>
            Error.NotFound("Report.SalesPartnerNotAssigned", message);

        public static Error LoadManagementSalesReportFailed(string message) =>
            Error.Failure("Report.LoadManagementSalesReportFailed", message);

        public static Error DesktopSalesReviewFailed(string message) =>
            Error.Failure("Report.DesktopSalesReviewFailed", message);
    }
}