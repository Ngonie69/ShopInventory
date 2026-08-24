namespace ShopInventory.Features.VanSalesOrders;

/// <summary>
/// The operator-set trading rules for customer app ordering, read together.
/// </summary>
/// <remarks>
/// One record rather than a method per setting because they are always wanted at the same moment —
/// building the catalogue needs the price list and the stock threshold, and the profile beside it
/// needs the cut-off — and three round trips to a settings table to answer one screen is three
/// chances for the answers to disagree.
/// </remarks>
/// <param name="CutOffHoursBeforeVisitDay">
/// Hours before midnight CAT on the visit day that ordering closes. 8 means 16:00 the day before.
/// </param>
/// <param name="PriceListNumber">
/// The SAP price list these customers buy on. They are all on one list, so this is a single number
/// rather than a per-customer lookup — but it is configurable, because which list that is, is a
/// commercial decision rather than a fact about the code.
/// </param>
/// <param name="LowStockThreshold">
/// At or below this quantity an item shows as low rather than in stock. A band, never a number:
/// a customer is told whether to expect their order to be filled, not what the depot holds.
/// </param>
public sealed record VanSalesOrderingRules(
    int CutOffHoursBeforeVisitDay,
    int PriceListNumber,
    decimal LowStockThreshold);
