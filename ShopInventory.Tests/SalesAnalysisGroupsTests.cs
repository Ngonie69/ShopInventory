using ShopInventory.Web.Components;

using Option = ShopInventory.Web.Components.SalesAnalysisPicker.Option;

namespace ShopInventory.Tests;

/// <summary>
/// The Group dropdown behind SAP's Main and Secondary Selection on /reports/item-volume: which of
/// SAP's groups are worth offering, and how a group code is matched.
/// </summary>
public class SalesAnalysisGroupsTests
{
    private static readonly Option[] Partners =
    [
        new("ABS006", "Abercorn Stores",   Group: "100"),
        new("BP0876", "Bulawayo Provisions", Group: "102"),
        new("BP0877", "Bindura Post",      Group: "102"),
        new("CRA001", "Cash Sale",         Group: null),
        new("TMP065", "Temporary Account", Group: "125")
    ];

    private static readonly (string Code, string? Name)[] SapGroups =
    [
        ("100", "Wholesale"),
        ("102", "Retail"),
        ("125", "Staff"),
        ("140", "Suppliers"),   // no member in the picker
        ("150", null)           // a member, but SAP holds no name
    ];

    [Fact]
    public void BuildOptions_offers_only_groups_something_belongs_to()
    {
        var options = SalesAnalysisGroups.BuildOptions(Partners, SapGroups);

        // 140 has no member here, so offering it would only ever empty the list. Membership is the
        // claim; the order they come out in is the next test's.
        Assert.Equal(
            new[] { "100", "102", "125" },
            options.Select(option => option.Value).OrderBy(code => code, StringComparer.Ordinal));
    }

    [Fact]
    public void BuildOptions_orders_by_name_rather_than_code()
    {
        var options = SalesAnalysisGroups.BuildOptions(Partners, SapGroups);

        Assert.Equal(new[] { "Retail", "Staff", "Wholesale" }, options.Select(option => option.Label));
    }

    [Fact]
    public void BuildOptions_keeps_an_unnamed_group_pickable()
    {
        var withUnnamed = Partners.Append(new Option("XYZ001", "Odd One", Group: "150")).ToList();

        var options = SalesAnalysisGroups.BuildOptions(withUnnamed, SapGroups);

        var unnamed = Assert.Single(options, option => option.Value == "150");
        Assert.Equal("Group 150", unnamed.Label);
    }

    [Fact]
    public void BuildOptions_is_empty_when_the_lookup_has_not_synced()
    {
        Assert.Empty(SalesAnalysisGroups.BuildOptions(Partners, Array.Empty<(string, string?)>()));
    }

    [Fact]
    public void BuildOptions_is_empty_when_nothing_carries_a_group()
    {
        var ungrouped = new[] { new Option("ABS006", "Abercorn Stores"), new Option("BP0876", "Bulawayo") };

        Assert.Empty(SalesAnalysisGroups.BuildOptions(ungrouped, SapGroups));
    }

    [Theory]
    [InlineData("102", "102")]
    [InlineData("  102  ", "102")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void Normalise_reduces_a_partner_group_code_to_matchable_text(string? code, string? expected) =>
        Assert.Equal(expected, SalesAnalysisGroups.Normalise(code));

    [Fact]
    public void Normalise_reduces_an_item_group_number_to_the_same_text()
    {
        // The two sides arrive from SAP as different types and have to end up comparable.
        Assert.Equal("102", SalesAnalysisGroups.Normalise(102));
        Assert.Equal(SalesAnalysisGroups.Normalise("102"), SalesAnalysisGroups.Normalise(102));
        Assert.Null(SalesAnalysisGroups.Normalise((int?)null));
    }

    [Fact]
    public void A_group_narrows_the_options_a_range_can_resolve_against()
    {
        // The property the picker relies on: narrowing to a group and then expanding a code range
        // expands inside that group, because the range only ever sees what the picker is offering.
        var group102 = Partners
            .Where(option => option.Group == "102")
            .Select(option => option.Value);

        var hits = SalesAnalysisCodeRange.FromBounds("ABS006", "TMP065", group102);

        Assert.Equal(new[] { "BP0876", "BP0877" }, hits);
    }
}
