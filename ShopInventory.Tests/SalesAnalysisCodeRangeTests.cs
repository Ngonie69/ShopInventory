using ShopInventory.Web.Components;

namespace ShopInventory.Tests;

/// <summary>
/// The code range behind both ways of asking for one on /reports/item-volume: typed into the search
/// box, or entered as SAP's Code from / to pair.
/// </summary>
/// <remarks>
/// The property under test throughout is that a range names only codes that exist. The picker shows
/// the count before the range is applied — "34 codes" — and a reader decides on that number, so a
/// range that resolved arithmetically and then quietly selected fewer would be lying at the one
/// moment it is being read.
/// </remarks>
public class SalesAnalysisCodeRangeTests
{
    /// <summary>A cache with gaps in it, mixed prefixes, and codes that are not a prefix and a number.</summary>
    private static readonly string[] Codes =
    [
        "ABS006",
        "BP0876", "BP0877", "BP0880", "BP1188", "BP1189",
        "BP9", "BP10",
        "CRA001", "CRA006",
        "MISC",
        "TMP065", "TMP100", "TMP128",
        "VAN008", "VAN019"
    ];

    [Fact]
    public void FromBounds_takes_the_codes_between_two_full_codes()
    {
        var hits = SalesAnalysisCodeRange.FromBounds("TMP065", "TMP128", Codes);

        Assert.Equal(new[] { "TMP065", "TMP100", "TMP128" }, hits);
    }

    [Fact]
    public void FromBounds_never_names_a_code_that_is_not_there()
    {
        var hits = SalesAnalysisCodeRange.FromBounds("BP0876", "BP1188", Codes);

        // 0878, 0879 and everything between 0880 and 1188 are absent from the cache.
        Assert.Equal(new[] { "BP0876", "BP0877", "BP0880", "BP1188" }, hits);
    }

    [Fact]
    public void FromBounds_spans_by_number_rather_than_by_text()
    {
        var hits = SalesAnalysisCodeRange.FromBounds("BP9", "BP10", Codes);

        // Compared as text BP9 sorts after BP10 and this range would be empty.
        Assert.Equal(new[] { "BP9", "BP10" }, hits);
    }

    [Fact]
    public void FromBounds_reads_a_bare_number_with_the_other_ends_prefix()
    {
        var hits = SalesAnalysisCodeRange.FromBounds("TMP065", "128", Codes);

        Assert.Equal(new[] { "TMP065", "TMP100", "TMP128" }, hits);
    }

    [Fact]
    public void FromBounds_takes_the_ends_the_wrong_way_round()
    {
        var hits = SalesAnalysisCodeRange.FromBounds("TMP128", "TMP065", Codes);

        Assert.Equal(new[] { "TMP065", "TMP100", "TMP128" }, hits);
    }

    [Fact]
    public void FromBounds_is_case_insensitive()
    {
        var hits = SalesAnalysisCodeRange.FromBounds("van008", "van019", Codes);

        Assert.Equal(new[] { "VAN008", "VAN019" }, hits);
    }

    [Fact]
    public void FromBounds_leaves_the_upper_end_open_when_it_is_blank()
    {
        var hits = SalesAnalysisCodeRange.FromBounds("CRA001", "", Codes);

        Assert.Equal(new[] { "CRA001", "CRA006" }, hits);
    }

    [Fact]
    public void FromBounds_leaves_the_lower_end_open_when_it_is_blank()
    {
        var hits = SalesAnalysisCodeRange.FromBounds("", "VAN019", Codes);

        Assert.Equal(new[] { "VAN008", "VAN019" }, hits);
    }

    [Fact]
    public void FromBounds_covers_nothing_when_both_ends_are_blank()
    {
        Assert.Empty(SalesAnalysisCodeRange.FromBounds("", "", Codes));
        Assert.Empty(SalesAnalysisCodeRange.FromBounds("  ", "\t", Codes));
    }

    [Fact]
    public void FromBounds_compares_as_text_when_an_end_is_not_a_number()
    {
        var hits = SalesAnalysisCodeRange.FromBounds("A", "C", Codes);

        // A to C is every A, B and C code — the upper end is a letter, so the Cs are inside it
        // rather than just past it. MISC and the rest are out.
        Assert.Equal(
            new[] { "ABS006", "BP0876", "BP0877", "BP0880", "BP10", "BP1188", "BP1189", "BP9", "CRA001", "CRA006" },
            hits);
    }

    [Fact]
    public void FromBounds_takes_everything_beginning_with_the_upper_end()
    {
        var hits = SalesAnalysisCodeRange.FromBounds("CRA", "MISC", Codes);

        // MISC itself is in, and it would be out if the upper end were compared strictly.
        Assert.Equal(new[] { "CRA001", "CRA006", "MISC" }, hits);
    }

    [Fact]
    public void FromBounds_compares_as_text_across_two_prefixes()
    {
        var hits = SalesAnalysisCodeRange.FromBounds("CRA001", "TMP100", Codes);

        Assert.Equal(new[] { "CRA001", "CRA006", "MISC", "TMP065", "TMP100" }, hits);
    }

    [Fact]
    public void FromBounds_covers_nothing_when_the_range_is_empty()
    {
        Assert.Empty(SalesAnalysisCodeRange.FromBounds("ZZZ001", "ZZZ999", Codes));
        Assert.Empty(SalesAnalysisCodeRange.FromBounds("TMP200", "TMP300", Codes));
    }

    [Theory]
    [InlineData("BP0876-1188")]
    [InlineData("bp0876 – bp1188")]
    [InlineData("BP0876 to BP1188")]
    [InlineData("BP1188-876")]
    public void FromWritten_takes_the_forms_a_reader_types(string written)
    {
        var hits = SalesAnalysisCodeRange.FromWritten(written, Codes);

        Assert.Equal(new[] { "BP0876", "BP0877", "BP0880", "BP1188" }, hits);
    }

    [Theory]
    [InlineData("")]
    [InlineData("BP0876")]
    [InlineData("Chibuku")]
    [InlineData("BP0876-")]
    public void FromWritten_is_not_a_range(string written) =>
        Assert.Empty(SalesAnalysisCodeRange.FromWritten(written, Codes));

    [Fact]
    public void Both_forms_agree_about_the_same_range()
    {
        Assert.Equal(
            SalesAnalysisCodeRange.FromWritten("TMP065-128", Codes),
            SalesAnalysisCodeRange.FromBounds("TMP065", "TMP128", Codes));
    }

    [Fact]
    public void An_empty_cache_yields_an_empty_range()
    {
        Assert.Empty(SalesAnalysisCodeRange.FromBounds("BP0876", "BP1188", Array.Empty<string>()));
        Assert.Empty(SalesAnalysisCodeRange.FromWritten("BP0876-1188", Array.Empty<string>()));
    }
}
