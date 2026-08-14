using ShopInventory.Common;

namespace ShopInventory.Tests;

/// <summary>
/// The property that matters is recurrence: the same item code must always land in the same
/// bucket, whatever else was asked for alongside it. That is what lets a SAP query object be
/// reused across requests instead of a new permanent OUQR row being minted per distinct set.
/// </summary>
public sealed class SqlItemCodePrefixCoverTests
{
    [Fact]
    public void Codes_in_one_family_collapse_to_a_single_bucket()
    {
        var prefixes = SqlItemCodePrefixCover.Cover(["CHE011", "CHE042", "CHE001"]);

        Assert.Equal(["CHE"], prefixes);
    }

    /// <summary>
    /// The whole point. Two requests sharing one item but differing in every other item still
    /// reuse that item's bucket, so the statement recurs rather than being minted afresh.
    /// </summary>
    [Fact]
    public void A_code_maps_to_the_same_bucket_whatever_it_is_requested_with()
    {
        var first = SqlItemCodePrefixCover.Cover(["CHE011", "NRI049"]);
        var second = SqlItemCodePrefixCover.Cover(["CHE011", "PIC003", "BUT015"]);

        Assert.Contains("CHE", first);
        Assert.Contains("CHE", second);
    }

    /// <summary>
    /// Order and duplication in the caller's list must not change the cover, or the same set
    /// requested two ways would produce two statements and defeat the reuse.
    /// </summary>
    [Fact]
    public void The_cover_is_independent_of_order_and_duplicates()
    {
        Assert.Equal(
            SqlItemCodePrefixCover.Cover(["PIC003", "CHE011", "NRI049"]),
            SqlItemCodePrefixCover.Cover(["NRI049", "CHE011", "PIC003", "CHE011", "NRI049"]));
    }

    [Fact]
    public void Case_and_surrounding_whitespace_do_not_split_a_bucket()
    {
        Assert.Equal(["CHE"], SqlItemCodePrefixCover.Cover(["che011", " CHE042 ", "Che001"]));
    }

    [Fact]
    public void Blank_codes_are_ignored()
    {
        Assert.Equal(["CHE"], SqlItemCodePrefixCover.Cover(["CHE011", "", "   ", null]));
    }

    [Fact]
    public void An_empty_request_covers_nothing()
    {
        Assert.Empty(SqlItemCodePrefixCover.Cover([]));
    }

    /// <summary>
    /// A code shorter than the bucket width contributes itself, so it is still matched by the
    /// <c>LIKE 'prefix%'</c> the caller builds rather than being dropped.
    /// </summary>
    [Fact]
    public void A_short_code_becomes_its_own_bucket()
    {
        Assert.Equal(["AB", "CHE"], SqlItemCodePrefixCover.Cover(["AB", "CHE011"]));
    }

    /// <summary>
    /// The bucket count is what bounds OUQR growth: it tracks the number of families, not the
    /// number of distinct subsets, which is what made the old <c>IN</c>-list shape unbounded.
    /// </summary>
    [Fact]
    public void The_bucket_count_tracks_families_not_request_size()
    {
        var manyCodesFewFamilies = Enumerable.Range(1, 200)
            .Select(number => $"CHE{number:000}")
            .Concat(Enumerable.Range(1, 200).Select(number => $"NRI{number:000}"))
            .ToList();

        Assert.Equal(["CHE", "NRI"], SqlItemCodePrefixCover.Cover(manyCodesFewFamilies));
    }

    [Theory]
    [InlineData("CHE011", "CHE", true)]
    [InlineData("che011", "CHE", true)]
    [InlineData(" CHE011 ", "CHE", true)]
    [InlineData("NRI049", "CHE", false)]
    [InlineData("", "CHE", false)]
    [InlineData(null, "CHE", false)]
    public void Bucket_membership_matches_how_the_surplus_is_filtered(string? itemCode, string prefix, bool expected)
    {
        Assert.Equal(expected, SqlItemCodePrefixCover.IsInBucket(itemCode, prefix));
    }

    [Fact]
    public void A_prefix_width_below_one_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SqlItemCodePrefixCover.Cover(["CHE011"], 0));
    }
}
