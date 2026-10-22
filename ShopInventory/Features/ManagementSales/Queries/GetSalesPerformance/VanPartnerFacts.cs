using ShopInventory.Features.VanSalesReports.Queries;

namespace ShopInventory.Features.ManagementSales.Queries.GetSalesPerformance;

/// <summary>
/// The van sales of the period and the one before, as the van fact reader hands them back, with what is
/// needed to name the van accounts they were invoiced to.
/// </summary>
/// <param name="Current">Every van sale in the period — the same facts the van performance report counts.</param>
/// <param name="Previous">Every van sale in the period before.</param>
/// <param name="AccountNames">The SAP name of each van account, by CardCode, where a sale recorded one.</param>
/// <param name="RepNames">Each rep's display name, by user id.</param>
public sealed record VanPartnerFacts(
    IReadOnlyList<VanSaleFact> Current,
    IReadOnlyList<VanSaleFact> Previous,
    IReadOnlyDictionary<string, string> AccountNames,
    IReadOnlyDictionary<Guid, string> RepNames)
{
    public static readonly VanPartnerFacts None = new([], [], new Dictionary<string, string>(), new Dictionary<Guid, string>());

    /// <summary>SAP's booked cost on the current period's van sales, for each van account's margin.</summary>
    internal VanSalesMargin? Margin { get; init; }
}
