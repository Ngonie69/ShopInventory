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

        public static Error LoadManagementSalesReportFailed(string message) =>
            Error.Failure("Report.LoadManagementSalesReportFailed", message);

        public static Error DesktopSalesReviewFailed(string message) =>
            Error.Failure("Report.DesktopSalesReviewFailed", message);
    }
}