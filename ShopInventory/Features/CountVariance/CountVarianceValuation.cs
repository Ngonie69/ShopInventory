using System.Globalization;
using ShopInventory.DTOs;
using ShopInventory.Models;

namespace ShopInventory.Features.CountVariance;

/// <summary>
/// Values an SAP inventory count's variance at selling price.
/// </summary>
/// <remarks>
/// <para>
/// The variance is SAP's own figure, taken as it stands: counted minus in-warehouse on the count date.
/// It is not recomputed, so the report and the B1 screen cannot disagree about a quantity.
/// </para>
/// <para>
/// A line nobody has counted carries a variance of zero in SAP. Treating that as a match would report
/// an open count as clean when it has simply not been walked yet, so such lines are kept apart.
/// </para>
/// <para>
/// A price of zero means the list does not sell the item — most SAP price lists carry a zero row for
/// everything they do not use — not that the item is free. Those lines stay in the report, flagged,
/// and out of the money totals, rather than being valued at nothing.
/// </para>
/// </remarks>
public static class CountVarianceValuation
{
    public static (List<CountVarianceLineDto> Lines, CountVarianceTotalsDto Totals) Value(
        IEnumerable<InventoryCountingLine> countLines,
        IReadOnlyDictionary<string, decimal> sellingPrices)
    {
        var lines = countLines
            .Where(line => !string.IsNullOrWhiteSpace(line.ItemCode))
            .OrderBy(line => line.VisualOrder ?? line.LineNumber)
            .ThenBy(line => line.LineNumber)
            .Select(line => ValueLine(line, sellingPrices))
            .ToList();

        var totals = new CountVarianceTotalsDto { LineCount = lines.Count };

        foreach (var line in lines)
        {
            switch (line.Status)
            {
                case CountVarianceLineStatus.Short:
                    totals.ShortLines++;
                    totals.ShortQuantity -= line.Variance;
                    totals.ShortValue += line.VarianceValue ?? 0m;
                    break;
                case CountVarianceLineStatus.Over:
                    totals.OverLines++;
                    totals.OverQuantity += line.Variance;
                    totals.OverValue += line.VarianceValue ?? 0m;
                    break;
                case CountVarianceLineStatus.Matched:
                    totals.MatchedLines++;
                    break;
                default:
                    totals.NotCountedLines++;
                    break;
            }

            if (line.SellingPrice is { } price)
            {
                totals.StockValue += Money(line.InWarehouseQuantity * price);
            }
            else
            {
                totals.UnpricedLines++;
                if (line.Variance != 0m)
                {
                    totals.UnvaluedVarianceLines++;
                }
            }
        }

        totals.NetValue = totals.ShortValue + totals.OverValue;
        return (lines, totals);
    }

    private static CountVarianceLineDto ValueLine(
        InventoryCountingLine line,
        IReadOnlyDictionary<string, decimal> sellingPrices)
    {
        var itemCode = line.ItemCode!.Trim();
        var counted = string.Equals(line.Counted, SapYesNo.Yes, StringComparison.OrdinalIgnoreCase);
        var variance = counted ? line.Variance : 0m;

        decimal? price = sellingPrices.TryGetValue(itemCode, out var listPrice) && listPrice > 0m
            ? listPrice
            : null;

        return new CountVarianceLineDto
        {
            RowNumber = (line.VisualOrder ?? line.LineNumber - 1) + 1,
            ItemCode = itemCode,
            ItemDescription = line.ItemDescription,
            WarehouseCode = line.WarehouseCode,
            UoMCode = line.UoMCode,
            InWarehouseQuantity = line.InWarehouseQuantity,
            CountedQuantity = counted ? line.CountedQuantity : null,
            Variance = variance,
            SellingPrice = price,
            VarianceValue = price is { } p ? Money(variance * p) : null,
            Status = !counted
                ? CountVarianceLineStatus.NotCounted
                : variance < 0m
                    ? CountVarianceLineStatus.Short
                    : variance > 0m
                        ? CountVarianceLineStatus.Over
                        : CountVarianceLineStatus.Matched
        };
    }

    /// <summary>SAP's counting status as the report shows it.</summary>
    public static string StatusLabel(string? documentStatus)
        => string.Equals(documentStatus, "cdsClosed", StringComparison.OrdinalIgnoreCase) ? "Closed" : "Open";

    /// <summary>A calendar date out of SAP's midnight timestamp, without letting a time zone move it a day.</summary>
    public static DateTime? ParseCountDate(string? countDate)
    {
        if (string.IsNullOrWhiteSpace(countDate) || countDate.Length < 10)
        {
            return null;
        }

        return DateTime.TryParseExact(
            countDate[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? DateTime.SpecifyKind(date, DateTimeKind.Unspecified)
            : null;
    }

    /// <summary><c>HH:mm</c> out of SAP's <c>HH:mm:ss</c>.</summary>
    public static string? FormatCountTime(string? countTime)
        => string.IsNullOrWhiteSpace(countTime) ? null : countTime.Length >= 5 ? countTime[..5] : countTime;

    private static decimal Money(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
