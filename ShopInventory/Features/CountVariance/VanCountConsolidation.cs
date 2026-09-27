using ShopInventory.DTOs;
using ShopInventory.Models;

namespace ShopInventory.Features.CountVariance;

/// <summary>
/// Adds the vans' counts up: one count per van, valued the way a single count is, then totalled
/// across the fleet and rolled up by item.
/// </summary>
/// <remarks>
/// <para>
/// <b>One count per van — its latest in the range.</b> A van recounted on Friday after Monday's count
/// has two documents, and adding both would count its stock twice. The later one stands; the earlier
/// are listed as superseded so nobody wonders where they went.
/// </para>
/// <para>
/// <b>A van's lines, not its document's.</b> A count is not tied to a warehouse, its lines are. A
/// document spanning two vans is split between them, and a line in a warehouse that is not a van is
/// ignored.
/// </para>
/// </remarks>
public static class VanCountConsolidation
{
    public static VanCountVarianceReportDto Consolidate(
        IReadOnlyDictionary<string, string?> vans,
        IReadOnlyList<InventoryCounting> counts,
        IReadOnlyDictionary<int, string> counterNames,
        IReadOnlyDictionary<string, decimal> sellingPrices)
    {
        var report = new VanCountVarianceReportDto { VanCount = vans.Count };
        var valuedLines = new List<CountVarianceLineDto>();

        foreach (var (van, rep) in vans.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            var candidates = counts
                .Where(count => LinesIn(count, van).Any())
                .OrderByDescending(count => count.CountDate, StringComparer.Ordinal)
                .ThenByDescending(count => count.CountTime, StringComparer.Ordinal)
                .ThenByDescending(count => count.DocumentEntry)
                .ToList();

            if (candidates.Count == 0)
            {
                report.VansNotCounted.Add(new VanWithoutCountDto { WarehouseCode = van, RepName = rep });
                continue;
            }

            var latest = candidates[0];
            var (lines, totals) = CountVarianceValuation.Value(LinesIn(latest, van), sellingPrices);
            valuedLines.AddRange(lines);

            report.Vans.Add(new VanCountVarianceDto
            {
                WarehouseCode = van,
                RepName = rep,
                Document = CounterNames.Summarise(latest, counterNames),
                Totals = totals,
                SupersededDocumentNumbers = candidates.Skip(1).Select(count => count.DocumentNumber).ToList()
            });
        }

        // Worst first: the van someone has to explain leads the table.
        report.Vans = report.Vans
            .OrderBy(van => van.Totals.NetValue)
            .ThenBy(van => van.WarehouseCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var van in report.Vans)
        {
            Add(report.Totals, van.Totals);
        }

        report.Items = RollUpByItem(valuedLines);
        return report;
    }

    private static IEnumerable<InventoryCountingLine> LinesIn(InventoryCounting count, string warehouse)
        => (count.InventoryCountingLines ?? [])
            .Where(line => string.Equals(line.WarehouseCode?.Trim(), warehouse, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Every item that came up short or over on at least one van. Matched and uncounted lines add
    /// nothing to explain, so they are left out.
    /// </summary>
    private static List<ItemCountVarianceDto> RollUpByItem(IEnumerable<CountVarianceLineDto> lines)
        => lines
            .Where(line => line.Status is CountVarianceLineStatus.Short or CountVarianceLineStatus.Over)
            .GroupBy(line => line.ItemCode, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var price = group.Select(line => line.SellingPrice).FirstOrDefault(value => value is not null);
                return new ItemCountVarianceDto
                {
                    ItemCode = group.Key,
                    ItemDescription = group.Select(line => line.ItemDescription).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)),
                    SellingPrice = price,
                    VansShort = group.Count(line => line.Status == CountVarianceLineStatus.Short),
                    VansOver = group.Count(line => line.Status == CountVarianceLineStatus.Over),
                    ShortQuantity = -group.Where(line => line.Variance < 0m).Sum(line => line.Variance),
                    OverQuantity = group.Where(line => line.Variance > 0m).Sum(line => line.Variance),
                    NetQuantity = group.Sum(line => line.Variance),
                    NetValue = price is null ? null : group.Sum(line => line.VarianceValue ?? 0m)
                };
            })
            .OrderBy(item => item.NetValue is null)
            .ThenBy(item => item.NetValue ?? 0m)
            .ThenBy(item => item.NetQuantity)
            .ThenBy(item => item.ItemCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static void Add(CountVarianceTotalsDto into, CountVarianceTotalsDto van)
    {
        into.LineCount += van.LineCount;
        into.ShortLines += van.ShortLines;
        into.OverLines += van.OverLines;
        into.MatchedLines += van.MatchedLines;
        into.NotCountedLines += van.NotCountedLines;
        into.UnpricedLines += van.UnpricedLines;
        into.UnvaluedVarianceLines += van.UnvaluedVarianceLines;
        into.ShortQuantity += van.ShortQuantity;
        into.OverQuantity += van.OverQuantity;
        into.ShortValue += van.ShortValue;
        into.OverValue += van.OverValue;
        into.NetValue += van.NetValue;
        into.StockValue += van.StockValue;
    }
}
