using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Sync.Commands.SyncItemTaxGroups;

/// <summary>
/// Copies every sellable item's VAT group from the SAP item master into <c>SapItemTaxGroups</c>.
/// </summary>
/// <remarks>
/// That table is what a till sale is taxed from and what <c>DesktopIntegration/tax/item-rates</c>
/// serves. <see cref="Services.SapItemTaxGroupWarmJob"/> sends this at 03:45 CAT; an admin sends it
/// from Settings when an item's tax group has just been changed in SAP and cannot wait for the night.
/// </remarks>
public sealed record SyncItemTaxGroupsCommand() : IRequest<ErrorOr<ItemTaxGroupSyncResult>>;

/// <param name="ItemsRead">Items SAP answered with a VAT group.</param>
/// <param name="Added">Items the table did not hold before.</param>
/// <param name="Changed">Items whose VAT group moved, with what it was and what it is now.</param>
/// <param name="UnconfiguredGroups">
/// VAT groups real items are on that have no rate in <c>Tax:RatesByTaxCode</c> or no tax id in
/// <c>Revmax:TaxIdMappings</c>, so those items are charged or declared at the standard rate.
/// </param>
/// <param name="CompletedAtUtc">When the table was written.</param>
public sealed record ItemTaxGroupSyncResult(
    int ItemsRead,
    int Added,
    IReadOnlyList<ItemTaxGroupChange> Changed,
    IReadOnlyList<string> UnconfiguredGroups,
    DateTime CompletedAtUtc);

public sealed record ItemTaxGroupChange(string ItemCode, string Was, string Now);
