using ShopInventory.Web.Components;
using ShopInventory.Web.Models;

namespace ShopInventory.Tests;

/// <summary>
/// Covers how the Price List page lays a sheet of prices out: SAP item-group headings while sorted
/// by code, one flat ranking otherwise.
/// </summary>
public sealed class PriceSheetRowsTests
{
    private static readonly ItemPriceByListDto[] Prices =
    [
        new() { ItemCode = "MLK001", ItemName = "Full Cream Milk 2L", Price = 2.65m },
        new() { ItemCode = "BUT002", ItemName = "Butter Unsalted Bulk", Price = 14.85m },
        new() { ItemCode = "BUT001", ItemName = "Butter Salted Bulk", Price = 14.85m },
        new() { ItemCode = "ZZZ999", ItemName = "Not in the product cache", Price = 1.00m },
        new() { ItemCode = "CHE001", ItemName = "Cheddar Cheese Block 1kg", Price = 9.80m },
    ];

    private static readonly Dictionary<string, string> Groups = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MLK001"] = "Milk",
        ["BUT001"] = "Butter",
        ["BUT002"] = "Butter",
        ["CHE001"] = "Cheese",
    };

    [Fact]
    public void Sorted_by_code_the_items_sit_under_their_group_with_unknown_items_last()
    {
        var rows = PriceSheetRows.Build(Prices, Groups, null, PriceSheetSort.Code, ascending: true);

        Assert.Equal(
            ["# Butter 2", "BUT001", "BUT002", "# Cheese 1", "CHE001", "# Milk 1", "MLK001", "# Other items 1", "ZZZ999"],
            Describe(rows));
    }

    [Fact]
    public void Descending_by_code_reverses_the_groups_but_keeps_unknown_items_last()
    {
        var rows = PriceSheetRows.Build(Prices, Groups, null, PriceSheetSort.Code, ascending: false);

        Assert.Equal(
            ["# Milk 1", "MLK001", "# Cheese 1", "CHE001", "# Butter 2", "BUT002", "BUT001", "# Other items 1", "ZZZ999"],
            Describe(rows));
    }

    [Fact]
    public void Sorted_by_price_the_sheet_is_one_ranking_with_no_headings()
    {
        var rows = PriceSheetRows.Build(Prices, Groups, null, PriceSheetSort.Price, ascending: false);

        // The tie at 14.85 falls back to the code, so the order is stable between renders.
        Assert.Equal(["BUT001", "BUT002", "CHE001", "MLK001", "ZZZ999"], Describe(rows));
    }

    [Fact]
    public void A_search_narrows_the_items_and_the_group_counts()
    {
        var rows = PriceSheetRows.Build(Prices, Groups, " salted ", PriceSheetSort.Code, ascending: true);

        Assert.Equal(["# Butter 2", "BUT001", "BUT002"], Describe(rows));
    }

    [Fact]
    public void Without_a_group_lookup_the_sheet_is_flat()
    {
        var rows = PriceSheetRows.Build(Prices, new Dictionary<string, string>(), null, PriceSheetSort.Code, ascending: true);

        Assert.Equal(["BUT001", "BUT002", "CHE001", "MLK001", "ZZZ999"], Describe(rows));
    }

    [Fact]
    public void Row_keys_are_unique()
    {
        var rows = PriceSheetRows.Build(Prices, Groups, null, PriceSheetSort.Code, ascending: true);

        // They are Blazor @key values, and a duplicate ends the circuit.
        Assert.Equal(rows.Count, rows.Select(row => row.Key).Distinct().Count());
    }

    private static List<string> Describe(IEnumerable<PriceSheetRow> rows) =>
        rows.Select(row => row.Group is { } group ? $"# {group} {row.GroupCount}" : row.Price!.ItemCode!).ToList();
}
