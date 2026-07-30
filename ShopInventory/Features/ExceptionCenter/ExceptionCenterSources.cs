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
    public const string PaymentCallback = "payment-callback";
    public const string PaymentCallbackRejection = "payment-callback-rejection";
    public const string CreditNoteFiscalization = "credit-note-fiscalization";

    /// <summary>Approval-gated direct transfers that failed to post to SAP. Guid keyed.</summary>
    public const string PendingInventoryTransferPost = "pending-inventory-transfer-post";

    /// <summary>Approved transfer request changes that failed to reach SAP. Guid keyed.</summary>
    public const string PendingTransferRequestEditApply = "pending-transfer-request-edit-apply";

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
            PaymentCallback => "payment callback",
            PaymentCallbackRejection => "rejected payment callback",
            CreditNoteFiscalization => "credit note fiscalization",
            PendingInventoryTransferPost => "approved transfer awaiting its SAP post",
            PendingTransferRequestEditApply => "approved transfer request change awaiting SAP",
            _ => "unrecognised source"
        };

    public static bool IsKnown(string? source)
        => Normalize(source) switch
        {
            InvoiceQueue
                or InventoryTransferQueue
                or MobileOrderPostProcessing
                or PaymentCallback
                or PaymentCallbackRejection
                or CreditNoteFiscalization
                or PendingInventoryTransferPost
                or PendingTransferRequestEditApply => true,
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
                or PendingInventoryTransferPost
                or PendingTransferRequestEditApply => true,
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
