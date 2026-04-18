using ErrorOr;

namespace ShopInventory.Common.Errors;

public static partial class Errors
{
    public static class DesktopSales
    {
        public static Error DuplicateSale(string externalRef) =>
            Error.Conflict("DesktopSales.Duplicate", $"A sale with reference '{externalRef}' already exists");

        public static Error SnapshotNotFound(string warehouseCode, DateTime date) =>
            Error.NotFound("DesktopSales.SnapshotNotFound",
                $"No stock snapshot found for warehouse '{warehouseCode}' on {date:yyyy-MM-dd}");

        public static Error SnapshotNotReady(string warehouseCode) =>
            Error.Failure("DesktopSales.SnapshotNotReady",
                $"Stock snapshot for warehouse '{warehouseCode}' is still being loaded");

        public static Error InsufficientStock(string itemCode, string warehouseCode, decimal requested, decimal available) =>
            Error.Validation("DesktopSales.InsufficientStock",
                $"Insufficient stock for {itemCode} in {warehouseCode}: requested {requested}, available {available}");

        public static Error FiscalizationFailed(string message) =>
            Error.Failure("DesktopSales.FiscalizationFailed", message);

        public static Error ConsolidationFailed(string cardCode, string message) =>
            Error.Failure("DesktopSales.ConsolidationFailed",
                $"Consolidation failed for {cardCode}: {message}");

        public static Error ConsolidationNotFound(int id) =>
            Error.NotFound("DesktopSales.ConsolidationNotFound",
                $"Consolidation with ID {id} not found");

        public static Error NoPendingSales =>
            Error.Failure("DesktopSales.NoPendingSales", "No pending sales found for consolidation");

        public static Error StockFetchFailed(string warehouseCode, string message) =>
            Error.Failure("DesktopSales.StockFetchFailed",
                $"Failed to fetch stock for warehouse '{warehouseCode}': {message}");

        public static Error ReportNotFound(DateTime date) =>
            Error.NotFound("DesktopSales.ReportNotFound",
                $"No sales data found for {date:yyyy-MM-dd}");

        public static Error TransferWebhookFailed(string message) =>
            Error.Failure("DesktopSales.TransferWebhookFailed", message);

        public static Error ConcurrencyConflict =>
            Error.Conflict("DesktopSales.ConcurrencyConflict",
                "Stock was modified by another transaction. Please retry.");

        public static Error SaleNotFound(string externalRef) =>
            Error.NotFound("DesktopSales.SaleNotFound",
                $"Sale with reference '{externalRef}' not found");
    }
}
