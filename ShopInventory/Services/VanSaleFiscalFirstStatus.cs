namespace ShopInventory.Services;

/// <summary>
/// Where a van sale got to on its way from a reservation to a fiscalised SAP invoice.
/// </summary>
/// <remarks>
/// The order matters to the caller more than the names do. Everything before
/// <see cref="AwaitingSap"/> left no fiscal receipt behind, so the sale can still be refused. From
/// <see cref="AwaitingSap"/> on, a receipt exists that cannot be withdrawn, and the sale stands whatever
/// SAP says. <see cref="FiscalUnresolved"/> is the one in between: nobody knows yet.
/// </remarks>
public enum VanSaleFiscalFirstStatus
{
    /// <summary>Fiscalised, then posted. The receipt and the invoice both exist.</summary>
    Posted,

    /// <summary>
    /// Fiscalised, and SAP has not taken the invoice yet. The receipt exists, so the sale stands; the
    /// reservation is left holding its stock and the post is to be retried, never the fiscalisation.
    /// </summary>
    AwaitingSap,

    /// <summary>The device refused the receipt outright. Nothing was signed and nothing was posted.</summary>
    FiscalFailed,

    /// <summary>
    /// The device could not be asked whether an earlier attempt already signed this sale, so nothing was
    /// sent. Safe to try again later; nothing is known to exist.
    /// </summary>
    FiscalUnchecked,

    /// <summary>
    /// The device may or may not have signed it. Never retried automatically, because a second receipt
    /// cannot be withdrawn; a person checks the device first.
    /// </summary>
    FiscalUnresolved,

    /// <summary>
    /// The sale cannot go this way at all — its reservation is gone, or its reference is taken by a
    /// different kind of sale. Nothing was signed by this call.
    /// </summary>
    NotPostable
}
