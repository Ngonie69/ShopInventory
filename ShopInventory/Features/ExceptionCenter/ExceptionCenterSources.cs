using System.Globalization;

namespace ShopInventory.Features.ExceptionCenter;

/// <summary>
/// The registry of everything the exception center aggregates.
/// </summary>
/// <remarks>
/// Sources are not keyed alike: the queues carry int primary keys, the approval-gated transfers
/// carry Guid ones. Every item therefore routes on a string <see cref="ExceptionCenterSources"/>
/// key — the decimal form of the id for int-keyed sources, so links and stored acknowledgements
/// written before Guid sources existed still resolve unchanged.
/// </remarks>
public static class ExceptionCenterSources
{
    public const string InvoiceQueue = "invoice-queue";
    public const string InventoryTransferQueue = "inventory-transfer-queue";
    public const string MobileOrderPostProcessing = "mobile-order-post-processing";

    /// <summary>Customer receipts failing to post to SAP. Int keyed.</summary>
    public const string IncomingPaymentQueue = "incoming-payment-queue";

    public const string PaymentCallback = "payment-callback";
    public const string PaymentCallbackRejection = "payment-callback-rejection";
    public const string CreditNoteFiscalization = "credit-note-fiscalization";

    /// <summary>Approval-gated direct transfers that failed to post to SAP. Guid keyed.</summary>
    public const string PendingInventoryTransferPost = "pending-inventory-transfer-post";

    /// <summary>Approved transfer request changes that failed to reach SAP. Guid keyed.</summary>
    public const string PendingTransferRequestEditApply = "pending-transfer-request-edit-apply";

    /// <summary>
    /// Fiscalised van sales that have not reached SAP. Int keyed, on the desktop sale's own id.
    /// </summary>
    /// <remarks>
    /// The only source whose rows are not all failures. A van sale carries the trading day the handset
    /// sold it on, and the posting job reaches back a bounded number of days, so a sale can fall out of
    /// every future run's reach without any pass ever having touched it — no error, no attempts, no
    /// trace. Those are exactly the ones nothing else would ever mention.
    /// </remarks>
    public const string VanSalePosting = "van-sale-posting";

    /// <summary>
    /// Fiscal days that have stopped on their way to ZIMRA. Int keyed, on the day's own state row.
    /// </summary>
    /// <remarks>
    /// The only source whose items are not documents. A stamped receipt reaches ZIMRA when its fiscal day
    /// is closed, packaged and uploaded, and until that happens nothing about the receipt looks wrong: the
    /// customer has it, SAP has the invoice, the platform has archived it. The failure is a day that stayed
    /// where it was, which is visible in exactly one place — the state row this lists.
    ///
    /// Retry is deliberately off. Half of what lands here is a close or an upload whose outcome FDMS never
    /// confirmed, and neither is idempotent there: the resolution is to read the device's status or the
    /// list of files FDMS accepted, which the lifecycle already does on every pass. A retry button would
    /// offer the one action that can make it worse.
    /// </remarks>
    public const string FiscalDayLifecycle = "fiscal-day-lifecycle";

    /// <summary>
    /// Signed van receipts that will never reach the platform on their own. Int keyed, on the sale's id.
    /// </summary>
    /// <remarks>
    /// A device is one hash chain, so a receipt that cannot be archived stops every receipt behind it —
    /// one broken row is a stopped van, not a stuck document. Retry is off for the same reason it is off in
    /// the drain: a chain break cannot be repaired by resending, because the signature is chained and the
    /// receipt cannot be re-signed.
    /// </remarks>
    public const string FiscalReceiptIngest = "fiscal-receipt-ingest";

    /// <summary>
    /// Signed van receipts this server never managed to store at all. Int keyed, on the incident row.
    /// </summary>
    /// <remarks>
    /// The one fiscal source that is not backed by the document it is about, and it has to be: there is no
    /// document. An online van sale posts its invoice to SAP inside the request and only then writes the
    /// receipt the handset signed; if that write fails the receipt exists on the device, on the customer's
    /// printout and nowhere else on this server.
    ///
    /// <para>
    /// Every other fiscal source is a row that can be listed by querying the thing that went wrong.
    /// <see cref="FiscalReceiptIngest"/> reads <c>DesktopSales</c>, <see cref="FiscalDayLifecycle"/> reads
    /// the day states. A receipt that was never stored has no row to find, which is also why the loss
    /// hides from <c>FiscalDayLifecycleService</c>: with nothing outstanding on the table, the device-day
    /// reads as settled, closes, uploads, and FDMS refuses it for a gap this server cannot see. So it is
    /// recorded as an incident at the moment it happens or it is not recorded anywhere.
    /// </para>
    /// </remarks>
    public const string VanSaleReceiptStorage = "van-sale-receipt-storage";

    public static string Normalize(string? source)
        => (source ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>The routing key for an int-keyed source's item.</summary>
    public static string Key(int itemId) => itemId.ToString(CultureInfo.InvariantCulture);

    /// <summary>The routing key for a Guid-keyed source's item.</summary>
    public static string Key(Guid itemId) => itemId.ToString("D");

    /// <summary>Plain-English name for a source, for error text and log lines.</summary>
    public static string DescribeSource(string? source)
        => Normalize(source) switch
        {
            InvoiceQueue => "invoice posting queue",
            InventoryTransferQueue => "inventory transfer posting queue",
            MobileOrderPostProcessing => "mobile order post-processing queue",
            IncomingPaymentQueue => "customer receipt posting queue",
            PaymentCallback => "payment callback",
            PaymentCallbackRejection => "rejected payment callback",
            CreditNoteFiscalization => "credit note fiscalization",
            PendingInventoryTransferPost => "approved transfer awaiting its SAP post",
            PendingTransferRequestEditApply => "approved transfer request change awaiting SAP",
            VanSalePosting => "van sale awaiting SAP",
            FiscalDayLifecycle => "fiscal day that has not reached ZIMRA",
            FiscalReceiptIngest => "signed receipt the platform has not taken",
            VanSaleReceiptStorage => "signed receipt this server failed to store",
            _ => "unrecognised source"
        };

    public static bool IsKnown(string? source)
        => Normalize(source) switch
        {
            InvoiceQueue
                or InventoryTransferQueue
                or MobileOrderPostProcessing
                or IncomingPaymentQueue
                or PaymentCallback
                or PaymentCallbackRejection
                or CreditNoteFiscalization
                or PendingInventoryTransferPost
                or PendingTransferRequestEditApply
                or VanSalePosting
                or FiscalDayLifecycle
                or FiscalReceiptIngest
                or VanSaleReceiptStorage => true,
            _ => false
        };

    /// <summary>
    /// Whether an item from this source can be pushed at its destination again. Sources that only
    /// record what a third party told us — a payment callback, a rejection — cannot.
    /// </summary>
    public static bool SupportsRetry(string? source)
        => Normalize(source) switch
        {
            InvoiceQueue
                or InventoryTransferQueue
                or MobileOrderPostProcessing
                or IncomingPaymentQueue
                or PendingInventoryTransferPost
                or PendingTransferRequestEditApply
                or VanSalePosting => true,
            _ => false
        };

    /// <summary>Whether this source's items are keyed by Guid rather than int.</summary>
    public static bool IsGuidKeyed(string? source)
        => Normalize(source) switch
        {
            PendingInventoryTransferPost or PendingTransferRequestEditApply => true,
            _ => false
        };

    /// <summary>
    /// Reads the int id back out of a routing key. Guid-keyed sources have no int id, so callers
    /// that still need one — the legacy <c>ItemId</c> column, log lines — get zero.
    /// </summary>
    public static int ToLegacyItemId(string? itemKey)
        => int.TryParse(itemKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out var itemId)
            ? itemId
            : 0;
}
