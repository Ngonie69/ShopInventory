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

        // --- Who is selling, and on whose behalf ---
        //
        // A till sells as the account it signed in as. The customer, the warehouse the stock leaves
        // and the cost centre it is booked to all come from that account, so an account missing one
        // cannot sell at all — better a clear refusal at the first sale than a day of takings booked
        // against the wrong business partner.

        public static Error Unauthenticated =>
            Error.Unauthorized("DesktopSales.Unauthenticated",
                "The sale could not be attributed to a signed-in user");

        public static Error MissingCustomerAssignment =>
            Error.Validation("DesktopSales.MissingCustomerAssignment",
                "This account has no assigned business partner, so it cannot sell. Ask an administrator to assign one.");

        public static Error MissingWarehouseAssignment =>
            Error.Validation("DesktopSales.MissingWarehouseAssignment",
                "This account has no assigned warehouse, so there is no stock for it to sell from. Ask an administrator to assign one.");

        /// <summary>
        /// The shop the account works at has been closed. Refused rather than allowed to trade on:
        /// a deactivated shop is one head office has stopped, and its till going on selling is the
        /// thing deactivating it was meant to prevent.
        /// </summary>
        public static Error ShopInactive(string shopName) =>
            Error.Validation("DesktopSales.ShopInactive",
                $"{shopName} is no longer trading, so this till cannot sell. Ask an administrator to reopen it or reassign this account.");

        /// <summary>
        /// The shop record is missing a value a sale cannot be built without.
        /// </summary>
        /// <remarks>
        /// Names the shop rather than the account, unlike the assignment errors above, because that is
        /// where the fix has to be made — and one bad shop stops every operator at it, so an operator
        /// told to check their own account would be sent to the wrong place.
        /// </remarks>
        public static Error ShopMisconfigured(string shopName, string field) =>
            Error.Validation("DesktopSales.ShopMisconfigured",
                $"{shopName} has no {field} configured, so this till cannot sell. Ask an administrator to complete the shop's setup.");

        /// <summary>
        /// Each business partner draws stock from its own warehouse, so an account holding several is
        /// a configuration mistake rather than a choice the till can be asked to make.
        /// </summary>
        public static Error AmbiguousWarehouseAssignment(int count) =>
            Error.Validation("DesktopSales.AmbiguousWarehouseAssignment",
                $"This account is assigned {count} warehouses. A selling account must be assigned exactly one.");

        /// <summary>
        /// The request named a customer or warehouse that is not the account's. Refused rather than
        /// silently corrected: a till that believes it sold from one warehouse while the server sold
        /// from another is the confusion deriving these from the account exists to remove.
        /// </summary>
        /// <summary>
        /// Vending invoices a named vendor, so a sale without one has nobody to bill.
        /// </summary>
        public static Error VendorRequired =>
            Error.Validation("DesktopSales.VendorRequired",
                "A vendor code is required for a vending sale.");

        /// <remarks>
        /// Deliberately does not distinguish "no such vendor" from "that vendor is deactivated". Both
        /// mean the same thing to the operator — it is not one you may invoice — and separating them
        /// would let a caller enumerate the vendors of a business partner it is not assigned to.
        /// </remarks>
        public static Error VendorNotAvailable(string vendorCode) =>
            Error.Validation("DesktopSales.VendorNotAvailable",
                $"Vendor '{vendorCode}' is not one this account can invoice. It may have been deactivated.");

        public static Error AssignmentMismatch(string field, string requested, string assigned) =>
            Error.Validation("DesktopSales.AssignmentMismatch",
                $"The request specified {field} '{requested}' but this account sells as '{assigned}'. Omit it and the account's own value is used.");

        // --- Who may read the takings ---
        //
        // The counterpart to the block above. Listing sales used to take its warehouse from the query
        // string and check it against nobody, so any authenticated staff account could read any shop's
        // money by editing one parameter. The warehouse comes from the account now.

        /// <summary>
        /// The account has no reason to read till takings at all.
        /// </summary>
        /// <remarks>
        /// Not a role list in the message. Naming which roles may read would tell a caller that should
        /// not be here what to become, and the operator who legitimately hits this needs an
        /// administrator either way.
        /// </remarks>
        public static Error SalesReadNotPermitted =>
            Error.Forbidden("DesktopSales.SalesReadNotPermitted",
                "This account is not permitted to read till sales.");

        /// <summary>
        /// A shop-scoped caller asked for somebody else's warehouse.
        /// </summary>
        /// <remarks>
        /// Refused rather than silently narrowed to their own. A till showing a page headed with one
        /// warehouse and filled with another's takings is worse than a refusal, and quietly rewriting
        /// the request would hide a client bug — or a probe — that somebody should see.
        /// </remarks>
        public static Error SalesReadOutsideScope(string requested, string assigned) =>
            Error.Forbidden("DesktopSales.SalesReadOutsideScope",
                $"This account can only read sales for warehouse '{assigned}', not '{requested}'.");
    }
}
