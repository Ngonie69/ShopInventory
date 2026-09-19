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

        /// <summary>
        /// The shared stock ledger would not cover this sale.
        /// </summary>
        /// <remarks>
        /// Distinct from <see cref="InsufficientStock"/> because the ledger counts what the whole
        /// system has promised today, not only what this till has sold. A cashier looking at a full
        /// shelf can now be refused because a web invoice took the same units a minute ago, and the
        /// message has to be able to say so.
        ///
        /// <para>
        /// A Validation error, so a 400. It was a Conflict, and a 409 is the one answer the till cannot
        /// show: it reads 409 as "an invoice for this reference already exists" and tells the cashier the
        /// sale may or may not have been created, putting the shortfall only in its audit row. A cashier
        /// refused for stock has to be told it was stock, and that nothing was sold.
        /// </para>
        /// </remarks>
        public static Error StockLedgerRefused(string detail) =>
            Error.Validation("DesktopSales.StockLedgerRefused", detail);

        /// <summary>
        /// SAP does not hold enough batch stock to accept this sale's invoice.
        /// </summary>
        /// <remarks>
        /// A Validation error, so a 400, like <see cref="StockLedgerRefused"/>. The till shows the server's
        /// reason only for a 400 or 422; a 409 reaches the cashier as "it may or may not have been
        /// created", which for a refusal is wrong in the one way that matters.
        /// </remarks>
        public static Error SapStockShort(string detail) =>
            Error.Validation("DesktopSales.SapStockShort", detail);

        public static Error FiscalizationFailed(string message) =>
            Error.Failure("DesktopSales.FiscalizationFailed", message);

        /// <summary>
        /// The caller asked for a sale that is not fiscalised, on a route where every sale must be.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>Fiscalize: false</c> used to be honoured on every source: the sale was written
        /// <c>Skipped</c>, and <c>Skipped</c> posted to SAP exactly as a fiscalised sale did — so a
        /// caller could put an A/R invoice in front of ZIMRA's back with one flag, and the only trace
        /// was the words "Not fiscalised" in the Remarks of a document nobody re-reads.
        /// </para>
        /// <para>
        /// A Validation error, so a 400, for the reason <see cref="StockLedgerRefused"/> gives: the
        /// till shows the server's reason only for a 400, and for any other status tells the cashier
        /// the sale may or may not have been created. Here it certainly was not.
        /// </para>
        /// </remarks>
        public static Error FiscalisationRequired(string sourceSystem) =>
            Error.Validation("DesktopSales.FiscalisationRequired",
                $"A {Describe(sourceSystem)} sale must be fiscalised, so it cannot be created with "
                + "fiscalisation switched off. Nothing was sold. Send the sale without 'fiscalize: false'.");

        private static string Describe(string sourceSystem) => sourceSystem switch
        {
            Sales.SaleSourceSystems.ShopTill => "shop till",
            Sales.SaleSourceSystems.Vending => "vending",
            var other => other
        };

        public static Error ConsolidationFailed(string cardCode, string message) =>
            Error.Failure("DesktopSales.ConsolidationFailed",
                $"Consolidation failed for {cardCode}: {message}");

        public static Error ConsolidationNotFound(int id) =>
            Error.NotFound("DesktopSales.ConsolidationNotFound",
                $"Consolidation with ID {id} not found");

        /// <remarks>
        /// Names the date and the other route because the usual reason for this answer is not an empty
        /// day. It is a day whose pending sales all came from tills, vending or vans, which this run
        /// never touches. Without the second sentence the console read it as "the run is broken".
        /// </remarks>
        public static Error NoPendingSales(DateTime date) =>
            Error.Failure("DesktopSales.NoPendingSales",
                $"Nothing on {date:dd MMM yyyy} is waiting for consolidation. Till, vending and van sales "
                + "are not consolidated: they post to SAP one invoice each, so select them in the list "
                + "and use Post to SAP.");

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

        // --- Posting a sale to SAP on request ---
        //
        // The manual and bulk levers on the desktop sales console. Every one of these is a refusal to
        // create a SAP document, so each says which sale it is about: a bulk post reports a row per
        // reference, and a message that named no sale would be unreadable in that list.

        /// <summary>
        /// This sale is not one that may be posted as an invoice of its own — see
        /// <see cref="Common.Sales.DesktopSalePostEligibility"/> for the rule and
        /// <paramref name="reason"/> for which clause of it refused.
        /// </summary>
        public static Error SaleNotPostable(string externalRef, string reason) =>
            Error.Validation("DesktopSales.SaleNotPostable", $"{externalRef}: {reason}");

        /// <summary>
        /// Somebody else is posting this sale right now — the background pass, or another person.
        /// </summary>
        /// <remarks>
        /// A conflict rather than a failure, and the distinction is the point: nothing went wrong and
        /// nothing was written, so the sale is neither retried nor marked. Whoever holds the claim
        /// finishes, and the console shows the result on its next poll.
        /// </remarks>
        public static Error SalePostInProgress(string externalRef) =>
            Error.Conflict("DesktopSales.SalePostInProgress",
                $"{externalRef}: a post to SAP for this sale is already in progress. Wait for it to finish rather than sending a second.");

        /// <summary>SAP refused the document, or could not be asked.</summary>
        public static Error SalePostFailed(string externalRef, string detail) =>
            Error.Failure("DesktopSales.SalePostFailed", $"{externalRef}: {detail}");

        // --- Retrying a sale's fiscalisation on request ---

        /// <summary>
        /// This sale may not be offered to the fiscal device again — see
        /// <see cref="Common.Sales.DesktopSaleFiscalisationRetry"/> for the rule and
        /// <paramref name="reason"/> for which clause of it refused.
        /// </summary>
        public static Error SaleNotFiscalisable(string externalRef, string reason) =>
            Error.Validation("DesktopSales.SaleNotFiscalisable", $"{externalRef}: {reason}");

        /// <summary>
        /// Nothing was sent: the device could not be asked whether it already holds this receipt.
        /// </summary>
        /// <remarks>
        /// Kept apart from <see cref="SaleFiscalisationFailed"/> because the sale was left exactly as it
        /// was — no attempt spent, no status changed — and pressing Retry again once the device answers
        /// is the whole remedy.
        /// </remarks>
        public static Error SaleFiscalisationUncheckable(string externalRef, string detail) =>
            Error.Failure("DesktopSales.SaleFiscalisationUncheckable", $"{externalRef}: {detail}");

        /// <summary>The device was asked to sign the receipt and did not.</summary>
        public static Error SaleFiscalisationFailed(string externalRef, string detail) =>
            Error.Failure("DesktopSales.SaleFiscalisationFailed", $"{externalRef}: {detail}");

        /// <summary>A bulk post that named nothing, or more references than one request may carry.</summary>
        public static Error BulkPostReferencesRequired =>
            Error.Validation("DesktopSales.BulkPostReferencesRequired",
                "Name at least one sale to post.");
    }
}
