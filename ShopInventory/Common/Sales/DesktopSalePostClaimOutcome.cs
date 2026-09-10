namespace ShopInventory.Common.Sales;

/// <summary>
/// What asking for the right to post one sale to SAP came back with.
/// </summary>
public enum DesktopSalePostClaimOutcome
{
    /// <summary>This caller owns the post. Nobody else may start one until it completes or releases.</summary>
    Granted,

    /// <summary>
    /// The post already completed and SAP holds the invoice. The document is on
    /// <see cref="DesktopSalePostClaim.Receipt"/>.
    /// </summary>
    AlreadyPosted,

    /// <summary>
    /// Somebody else is posting this sale right now — the background job and a person pressing Post,
    /// or two people pressing it. Nothing is sent.
    /// </summary>
    InFlight
}
