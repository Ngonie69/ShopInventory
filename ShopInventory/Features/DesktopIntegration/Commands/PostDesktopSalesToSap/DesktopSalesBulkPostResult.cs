using ShopInventory.Features.DesktopIntegration.Commands.PostDesktopSaleToSap;

namespace ShopInventory.Features.DesktopIntegration.Commands.PostDesktopSalesToSap;

/// <summary>
/// What a bulk post did, sale by sale.
/// </summary>
/// <remarks>
/// The counts are derived from <see cref="Results"/> rather than accumulated alongside it, so a
/// summary that disagrees with the rows it summarises is not expressible.
/// </remarks>
public sealed record DesktopSalesBulkPostResult(IReadOnlyList<DesktopSalePostResult> Results)
{
    public int Requested => Results.Count;

    public int Posted => Count(DesktopSalePostOutcomes.Posted);

    public int AlreadyInSap => Count(DesktopSalePostOutcomes.AlreadyInSap);

    /// <summary>
    /// Sales another post already held. Not failures: nothing was sent and nothing was written, and
    /// whoever holds the claim is finishing the job. Kept visible so a run that reports four of five
    /// posted says which one it left alone and why.
    /// </summary>
    public int InProgress => Count(DesktopSalePostOutcomes.InProgress);

    public int Failed => Count(DesktopSalePostOutcomes.Failed);

    public int NotPostable => Count(DesktopSalePostOutcomes.NotPostable);

    private int Count(string outcome) =>
        Results.Count(result => string.Equals(result.Outcome, outcome, StringComparison.Ordinal));
}
