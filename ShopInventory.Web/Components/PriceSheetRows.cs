using ShopInventory.Web.Models;

namespace ShopInventory.Web.Components;

/// <summary>The column the Price List page's sheet is sorted by.</summary>
public enum PriceSheetSort { Code, Name, Price }

/// <summary>
/// One line of the Price List page's sheet: an item group's heading (<see cref="Group"/> set) or an
/// item's price (<see cref="Price"/> set).
/// </summary>
public sealed record PriceSheetRow(string Key, string? Group, int GroupCount, ItemPriceByListDto? Price);

/// <summary>
/// Filters, sorts and flattens a price list — or a customer's prices — into the rows /prices draws.
/// </summary>
/// <remarks>
/// Grouped by SAP item group only while sorted by code, as `Price List.dc.html` does: sorted by
/// name or price the reader is ranking across the whole list, and headings would break the ranking
/// up. An item whose group is unknown gathers under <see cref="OtherGroup"/>, last whichever way
/// the groups run. With no group lookup at all the sheet is flat.
/// </remarks>
public static class PriceSheetRows
{
    public const string OtherGroup = "Other items";

    public static List<PriceSheetRow> Build(
        IEnumerable<ItemPriceByListDto> prices,
        IReadOnlyDictionary<string, string> groupByItemCode,
        string? searchTerm,
        PriceSheetSort sortBy,
        bool ascending)
    {
        var term = searchTerm?.Trim();
        var matches = prices
            .Where(price => string.IsNullOrEmpty(term) ||
                (price.ItemCode?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (price.ItemName?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();

        IEnumerable<ItemPriceByListDto> Sorted(IEnumerable<ItemPriceByListDto> items) => (sortBy, ascending) switch
        {
            (PriceSheetSort.Name, true) => items.OrderBy(price => price.ItemName, StringComparer.OrdinalIgnoreCase),
            (PriceSheetSort.Name, false) => items.OrderByDescending(price => price.ItemName, StringComparer.OrdinalIgnoreCase),
            (PriceSheetSort.Price, true) => items.OrderBy(price => price.Price).ThenBy(price => price.ItemCode, StringComparer.OrdinalIgnoreCase),
            (PriceSheetSort.Price, false) => items.OrderByDescending(price => price.Price).ThenBy(price => price.ItemCode, StringComparer.OrdinalIgnoreCase),
            (_, true) => items.OrderBy(price => price.ItemCode, StringComparer.OrdinalIgnoreCase),
            (_, false) => items.OrderByDescending(price => price.ItemCode, StringComparer.OrdinalIgnoreCase)
        };

        if (sortBy != PriceSheetSort.Code || groupByItemCode.Count == 0)
        {
            return Sorted(matches)
                .Select((price, index) => new PriceSheetRow($"i{index}:{price.ItemCode}", null, 0, price))
                .ToList();
        }

        var groups = matches
            .GroupBy(price => price.ItemCode != null && groupByItemCode.TryGetValue(price.ItemCode, out var name)
                ? name
                : OtherGroup)
            .OrderBy(group => group.Key == OtherGroup);
        var ordered = ascending
            ? groups.ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            : groups.ThenByDescending(group => group.Key, StringComparer.OrdinalIgnoreCase);

        var rows = new List<PriceSheetRow>();
        foreach (var group in ordered)
        {
            var members = group.ToList();
            rows.Add(new PriceSheetRow($"g:{group.Key}", group.Key, members.Count, null));
            rows.AddRange(Sorted(members)
                .Select((price, index) => new PriceSheetRow($"i:{group.Key}:{index}:{price.ItemCode}", null, 0, price)));
        }

        return rows;
    }
}
