namespace ShopInventory.Features.SalesOrders.Commands.BackfillWebOrderTax;

/// <summary>
/// What the backfill found and what it changed. The unposted and posted counts are reported
/// separately because they were repaired from different sources of truth.
/// </summary>
/// <param name="OrdersAffected">Web orders carrying no tax and at least one line.</param>
/// <param name="UnpostedOrdersFound">Of those, the ones SAP has never seen.</param>
/// <param name="UnpostedOrdersUpdated">Unposted orders recomputed at the configured VAT rate.</param>
/// <param name="UnpostedLinesUpdated">Unposted lines that gained the configured VAT rate.</param>
/// <param name="PostedOrdersFound">Of those, the ones already linked to a SAP document.</param>
/// <param name="PostedOrdersQueried">Posted orders this run read back from SAP, after the cap.</param>
/// <param name="PostedOrdersRepaired">Posted orders whose local mirror changed to match SAP.</param>
/// <param name="PostedLinesRepaired">Posted lines that gained their document's effective tax rate.</param>
/// <param name="PostedOrdersUnresolved">Posted orders SAP could not be read for; retry the run.</param>
/// <param name="PostedOrdersRemaining">Posted orders left for a later run because of the cap.</param>
/// <param name="ConfiguredTaxPercent">The VAT rate applied to unposted orders.</param>
/// <param name="DryRun">True when nothing was written.</param>
public sealed record BackfillWebOrderTaxResult(
    int OrdersAffected,
    int UnpostedOrdersFound,
    int UnpostedOrdersUpdated,
    int UnpostedLinesUpdated,
    int PostedOrdersFound,
    int PostedOrdersQueried,
    int PostedOrdersRepaired,
    int PostedLinesRepaired,
    int PostedOrdersUnresolved,
    int PostedOrdersRemaining,
    decimal ConfiguredTaxPercent,
    bool DryRun
);
