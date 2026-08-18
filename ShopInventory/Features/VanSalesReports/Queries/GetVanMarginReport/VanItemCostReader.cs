using System.Globalization;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesReports.Queries.GetVanMarginReport;

/// <summary>
/// What each item cost, per van warehouse, taken from the invoice lines SAP posted.
/// </summary>
/// <remarks>
/// The cost is <c>INV1."StockPrice"</c> — the inventory cost B1 stamped on the line when it posted,
/// which is the actual cost of those goods on that day rather than today's moving average. That
/// distinction is the reason this reads invoice lines at all instead of the far cheaper item master:
/// a current average cost applied to last month's sales is a grain error, and it is the same one
/// that made the price-benchmark report unbuildable in Phase 4.
///
/// <b>Two SAP objects, both under fixed codes.</b> A <c>SQLQueries</c> object cannot practically be
/// deleted, so a code that varies per request leaves a permanent row behind every time. These are
/// constant, and the values that move are bound as parameters — which is also why the warehouse is
/// one call per van rather than an <c>IN</c> list, since a parameter cannot carry one.
///
/// <b>Why the local currency is read at all.</b> <c>StockPrice</c> is stated in the company's local
/// currency while a line's revenue is in the document's, and this company bills in two. Subtracting
/// one from the other would produce a margin that looks entirely plausible and is arithmetic between
/// two kinds of money. Nothing in this codebase knew what the local currency was, so it is asked
/// for; where it cannot be established, no margin is computed and the report says why.
///
/// <b>Everything here degrades rather than throws.</b> A SAP failure, a rejected statement, an
/// unreadable currency — each returns an empty or unknown result, and the report renders with its
/// costable share and no margin. A margin report that 500s when SAP is down is worse than one that
/// says the cost is unavailable, because the rest of the page is local and still true.
/// </remarks>
public static class VanItemCostReader
{
    /// <summary>The company's local currency, which is the currency <c>StockPrice</c> is stated in.</summary>
    private const string LocalCurrencyCode = "VANMARGIN_LOCALCCY";

    private const string LocalCurrencySql =
        """SELECT T0."MainCurncy" AS "MainCurncy" FROM OADM T0""";

    private const string ItemCostCode = "VANMARGIN_ITEMCOST";

    /// <summary>
    /// Posted invoice lines for one warehouse over a date range.
    /// </summary>
    /// <remarks>
    /// Line grain, deliberately. The figure wanted is the cost of what sold, which is quantity times
    /// unit cost summed — and SAP's validator rejects arithmetic between two columns, so the
    /// multiplication has to happen here. Nothing else in the statement would trouble it: no
    /// <c>CASE</c>, no <c>COALESCE</c>, no function around a compared column, and dates bound as
    /// parameters in <c>yyyy-MM-dd</c>, which is the only form B1 matches on.
    /// </remarks>
    private const string ItemCostSql =
        """
        SELECT
            T1."ItemCode" AS "ItemCode",
            T1."WhsCode" AS "WhsCode",
            T0."DocCur" AS "DocCur",
            T1."Quantity" AS "Quantity",
            T1."StockPrice" AS "StockPrice"
        FROM OINV T0
        INNER JOIN INV1 T1 ON T0."DocEntry" = T1."DocEntry"
        WHERE T0."DocDate" >= :fromDate
          AND T0."DocDate" <= :toDate
          AND T1."WhsCode" = :warehouseCode
        """;

    /// <summary>
    /// The cost of one item on one van, and the currency that cost is stated in.
    /// </summary>
    /// <remarks>
    /// A weighted unit cost rather than a simple average: an item bought cheaply in bulk and dearly
    /// in ones has a cost that depends on how much of each sold, and averaging the two prices
    /// unweighted would answer a question nobody asked.
    /// </remarks>
    public sealed record ItemCost(string WarehouseCode, string ItemCode, decimal UnitCost, decimal Quantity);

    /// <summary>
    /// What the reader managed to establish. <see cref="Currency"/> is null when the local currency
    /// could not be read, and no margin may be computed in that case whatever else came back.
    /// </summary>
    public sealed record CostSet(string? Currency, Dictionary<(string Warehouse, string Item), ItemCost> Costs)
    {
        public static CostSet Unavailable => new(null, []);

        public bool CanCost => Currency is not null && Costs.Count > 0;

        public ItemCost? For(string? warehouse, string itemCode) =>
            warehouse is not null
            && Costs.TryGetValue((warehouse.Trim().ToUpperInvariant(), itemCode.Trim().ToUpperInvariant()),
                out var cost)
                ? cost
                : null;
    }

    public static async Task<CostSet> LoadAsync(
        ISAPServiceLayerClient sapClient,
        IReadOnlyCollection<string> warehouseCodes,
        DateTime from,
        DateTime to,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (warehouseCodes.Count == 0)
        {
            return CostSet.Unavailable;
        }

        var currency = await LoadLocalCurrencyAsync(sapClient, logger, cancellationToken);

        if (currency is null)
        {
            // Without knowing what StockPrice is denominated in, a margin cannot be stated. Stop
            // rather than fetch costs nothing may be done with.
            return CostSet.Unavailable;
        }

        var costs = new Dictionary<(string, string), ItemCost>();

        foreach (var warehouse in warehouseCodes)
        {
            var rows = await LoadWarehouseCostsAsync(sapClient, warehouse, from, to, logger, cancellationToken);

            foreach (var cost in Aggregate(warehouse, rows))
            {
                costs[(cost.WarehouseCode, cost.ItemCode)] = cost;
            }
        }

        return new CostSet(currency, costs);
    }

    private static async Task<string?> LoadLocalCurrencyAsync(
        ISAPServiceLayerClient sapClient,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var rows = await sapClient.ExecuteRawSqlQueryAsync(
                LocalCurrencyCode,
                "Van margin - company local currency",
                LocalCurrencySql,
                cancellationToken);

            var value = rows.FirstOrDefault()?.GetValueOrDefault("MainCurncy")?.ToString()?.Trim();

            return string.IsNullOrWhiteSpace(value) ? null : value.ToUpperInvariant();
        }
        catch (Exception ex)
        {
            // Includes SAP refusing the statement outright, which is how a wrong column name arrives.
            logger.LogWarning(ex, "Could not read the company local currency; van margin will not be costed");
            return null;
        }
    }

    private static async Task<List<Dictionary<string, object?>>> LoadWarehouseCostsAsync(
        ISAPServiceLayerClient sapClient,
        string warehouse,
        DateTime from,
        DateTime to,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            return await sapClient.ExecuteParameterisedSqlQueryAsync(
                ItemCostCode,
                "Van margin - posted invoice line cost by warehouse",
                ItemCostSql,
                new Dictionary<string, string>
                {
                    // yyyy-MM-dd. yyyyMMdd is accepted and silently matches nothing.
                    ["fromDate"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["toDate"] = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["warehouseCode"] = warehouse
                },
                cancellationToken);
        }
        catch (Exception ex)
        {
            // One van failing must not lose the others: the report names how much it could cost.
            logger.LogWarning(ex, "Could not read invoice line costs for warehouse {Warehouse}", warehouse);
            return [];
        }
    }

    private static IEnumerable<ItemCost> Aggregate(string warehouse, List<Dictionary<string, object?>> rows) =>
        rows
            .Select(row => new
            {
                ItemCode = row.GetValueOrDefault("ItemCode")?.ToString()?.Trim(),
                Quantity = ToDecimal(row.GetValueOrDefault("Quantity")),
                StockPrice = ToDecimal(row.GetValueOrDefault("StockPrice"))
            })
            .Where(row => !string.IsNullOrWhiteSpace(row.ItemCode) && row.Quantity > 0)
            .GroupBy(row => row.ItemCode!.ToUpperInvariant())
            .Select(group =>
            {
                var quantity = group.Sum(row => row.Quantity);
                var cost = group.Sum(row => row.Quantity * row.StockPrice);

                return new ItemCost(
                    WarehouseCode: warehouse.Trim().ToUpperInvariant(),
                    ItemCode: group.Key,
                    // Weighted, and guarded: the Where above keeps the divisor positive.
                    UnitCost: decimal.Round(cost / quantity, 6),
                    Quantity: quantity);
            })
            // A cost of zero is not a cost. B1 leaves StockPrice at zero on a line whose item has no
            // valuation yet, and carrying that through would report the item as pure margin.
            .Where(cost => cost.UnitCost > 0);

    private static decimal ToDecimal(object? value) =>
        value switch
        {
            null => 0m,
            decimal decimalValue => decimalValue,
            double doubleValue => (decimal)doubleValue,
            _ => decimal.TryParse(
                value.ToString(),
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out var parsed)
                ? parsed
                : 0m
        };
}
