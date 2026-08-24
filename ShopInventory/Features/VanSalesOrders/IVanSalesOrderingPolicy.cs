namespace ShopInventory.Features.VanSalesOrders;

/// <summary>
/// The operator-set rules governing when a van sales customer may order and on what terms.
/// </summary>
/// <remarks>
/// Read from <c>SystemConfigs</c> rather than <c>appsettings</c> because these are trading
/// decisions rather than deployment ones: moving the cut-off an hour, or switching the price list
/// these customers buy on, is something an operations manager should be able to do without waiting
/// for a release and an IIS restart.
/// </remarks>
public interface IVanSalesOrderingPolicy
{
    /// <summary>
    /// The current rules. Every value falls back to a documented default rather than failing, so a
    /// missing or mistyped settings row degrades to sane trading instead of an outage.
    /// </summary>
    Task<VanSalesOrderingRules> GetRulesAsync(CancellationToken cancellationToken);
}
