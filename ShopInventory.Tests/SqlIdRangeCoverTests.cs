using System.Globalization;
using ShopInventory.Common;
using ShopInventory.Services;

namespace ShopInventory.Tests;

public class SqlIdRangeCoverTests
{
    [Fact]
    public void Ranges_are_aligned_to_the_bucket_so_they_recur_across_requests()
    {
        // The whole point: two different requests touching neighbouring ids must produce the
        // same range, otherwise SAP gets another permanent query object per request.
        var first = SqlIdRangeCover.Cover([1024, 1099], bucketSize: 1000);
        var second = SqlIdRangeCover.Cover([1500, 1001, 1999], bucketSize: 1000);

        Assert.Equal([(1000, 1999)], first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Every_requested_id_falls_inside_a_returned_range()
    {
        // Correctness of the in-memory filter depends on the cover being complete: an id that
        // falls outside every range would silently vanish from the result.
        int[] ids = [1, 999, 1000, 1001, 4321, 99_999, 100_000];

        var ranges = SqlIdRangeCover.Cover(ids);

        foreach (var id in ids)
        {
            Assert.Contains(ranges, range => id >= range.Start && id <= range.End);
        }
    }

    [Fact]
    public void Ids_spanning_several_buckets_produce_one_range_each_ordered()
    {
        var ranges = SqlIdRangeCover.Cover([2500, 500, 1500], bucketSize: 1000);

        Assert.Equal([(0, 999), (1000, 1999), (2000, 2999)], ranges);
    }

    [Fact]
    public void Duplicate_ids_in_the_same_bucket_collapse_to_one_range()
    {
        var ranges = SqlIdRangeCover.Cover([1200, 1200, 1201, 1999], bucketSize: 1000);

        Assert.Single(ranges);
    }

    [Fact]
    public void Non_positive_ids_are_ignored()
    {
        // Callers treat 0 and negatives as "unset"; a bucket for them would query rubbish.
        Assert.Empty(SqlIdRangeCover.Cover([0, -5]));
        Assert.Equal([(1000, 1999)], SqlIdRangeCover.Cover([0, -1, 1500], bucketSize: 1000));
    }

    [Fact]
    public void Empty_input_produces_no_ranges_so_no_query_runs()
    {
        Assert.Empty(SqlIdRangeCover.Cover([]));
    }

    [Fact]
    public void Bucket_size_must_be_positive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SqlIdRangeCover.Cover([1], bucketSize: 0));
    }

    [Fact]
    public void Same_digit_width_ranges_never_mix_number_lengths()
    {
        // RIN1.BaseRef holds the document number as text, so the range is compared
        // lexicographically. '90' < '100' is false as text, so a range that spanned both widths
        // would silently miss rows.
        var ranges = SqlIdRangeCover.CoverSameDigitWidth([5, 42, 700], bucketSize: 1000);

        foreach (var (start, end) in ranges)
        {
            Assert.Equal(
                start.ToString(CultureInfo.InvariantCulture).Length,
                end.ToString(CultureInfo.InvariantCulture).Length);
        }
    }

    [Fact]
    public void Same_digit_width_cover_is_still_complete()
    {
        int[] ids = [1, 9, 10, 99, 100, 999, 1000, 12_345];

        var ranges = SqlIdRangeCover.CoverSameDigitWidth(ids);

        foreach (var id in ids)
        {
            Assert.Contains(ranges, range => id >= range.Start && id <= range.End);
        }
    }

    [Fact]
    public void Same_digit_width_ranges_compare_correctly_as_text()
    {
        // The property the SQL actually relies on: for every returned range, text comparison
        // agrees with numeric comparison for the ids inside it.
        int[] ids = [7, 88, 512, 4096];

        foreach (var (start, end) in SqlIdRangeCover.CoverSameDigitWidth(ids))
        {
            foreach (var id in ids.Where(candidate => candidate >= start && candidate <= end))
            {
                var text = id.ToString(CultureInfo.InvariantCulture);
                var lo = start.ToString(CultureInfo.InvariantCulture);
                var hi = end.ToString(CultureInfo.InvariantCulture);

                Assert.True(
                    string.CompareOrdinal(text, lo) >= 0 && string.CompareOrdinal(text, hi) <= 0,
                    $"'{text}' should sort within '{lo}'..'{hi}'");
            }
        }
    }
}

public class SqlMonthRangeCoverTests
{
    [Fact]
    public void A_range_inside_one_month_yields_that_month()
    {
        var months = SqlMonthRangeCover.CoverMonths(new DateTime(2026, 7, 9), new DateTime(2026, 7, 21));

        Assert.Equal([(new DateTime(2026, 7, 1), new DateTime(2026, 7, 31))], months);
    }

    [Fact]
    public void A_range_spanning_months_yields_each_whole_month()
    {
        var months = SqlMonthRangeCover.CoverMonths(new DateTime(2026, 1, 20), new DateTime(2026, 3, 2));

        Assert.Equal(
            [
                (new DateTime(2026, 1, 1), new DateTime(2026, 1, 31)),
                (new DateTime(2026, 2, 1), new DateTime(2026, 2, 28)),
                (new DateTime(2026, 3, 1), new DateTime(2026, 3, 31))
            ],
            months);
    }

    [Fact]
    public void Leap_february_ends_on_the_29th()
    {
        var months = SqlMonthRangeCover.CoverMonths(new DateTime(2028, 2, 3), new DateTime(2028, 2, 4));

        Assert.Equal([(new DateTime(2028, 2, 1), new DateTime(2028, 2, 29))], months);
    }

    [Fact]
    public void The_requested_window_always_falls_inside_the_covered_months()
    {
        // Callers filter the surplus days in memory, so the cover only has to be complete.
        var from = new DateTime(2026, 5, 14);
        var to = new DateTime(2026, 8, 3);

        var months = SqlMonthRangeCover.CoverMonths(from, to);

        Assert.True(months[0].Start <= from);
        Assert.True(months[^1].End >= to);
    }

    [Fact]
    public void An_inverted_range_yields_nothing_so_no_query_runs()
    {
        Assert.Empty(SqlMonthRangeCover.CoverMonths(new DateTime(2026, 7, 10), new DateTime(2026, 7, 1)));
    }
}

public class SapSqlCanonicalisationTests
{
    [Fact]
    public void Trailing_semicolon_does_not_change_the_canonical_form()
    {
        // Regression: CreateSqlQueryAsync sends NormalizeSapSqlText(sql), which strips the
        // terminator, so SAP stores it without one. If the reuse check compares the caller's
        // raw string instead, it never matches what is stored and every call PATCHes - the
        // exact 30s write the content-addressing was introduced to avoid.
        Assert.Equal(
            SAPServiceLayerClient.NormalizeSqlText("SELECT 1 FROM OJDT"),
            SAPServiceLayerClient.NormalizeSqlText("SELECT 1 FROM OJDT;"));
    }

    [Fact]
    public void Crlf_and_bare_cr_canonicalise_together()
    {
        // SAP rewrites CRLF to bare CR in storage, so the two must be indistinguishable here.
        Assert.Equal(
            SAPServiceLayerClient.NormalizeSqlText("SELECT 1\r\nFROM OJDT"),
            SAPServiceLayerClient.NormalizeSqlText("SELECT 1\rFROM OJDT"));
    }

    [Fact]
    public void Semicolon_and_newline_rewriting_combine()
    {
        // The real customer-statement case: verbatim string, CRLF endings, terminator on the end.
        Assert.Equal(
            SAPServiceLayerClient.NormalizeSqlText("SELECT 1\nFROM OJDT\nORDER BY \"RefDate\""),
            SAPServiceLayerClient.NormalizeSqlText("SELECT 1\r\nFROM OJDT\r\nORDER BY \"RefDate\";\r\n"));
    }

    [Fact]
    public void Canonical_form_is_idempotent()
    {
        // Stored text is re-canonicalised on every reuse check; a non-idempotent transform would
        // drift and start PATCHing again.
        var once = SAPServiceLayerClient.NormalizeSqlText("SELECT 1\r\nFROM OJDT;\r\n");

        Assert.Equal(once, SAPServiceLayerClient.NormalizeSqlText(once));
    }

    [Fact]
    public void Equivalent_statements_map_to_the_same_query_code()
    {
        // Same statement written two ways must reuse one SAP object, not create two.
        Assert.Equal(
            SAPServiceLayerClient.BuildContentAddressedQueryCode("PODCRA", "SELECT 1 FROM INV1"),
            SAPServiceLayerClient.BuildContentAddressedQueryCode("PODCRA", "SELECT 1 FROM INV1;\r\n"));
    }

    [Fact]
    public void Different_statements_map_to_different_query_codes()
    {
        // The safety property behind sharing a code: a shared code must imply identical SQL.
        Assert.NotEqual(
            SAPServiceLayerClient.BuildContentAddressedQueryCode("PODCRA", "SELECT 1 FROM INV1"),
            SAPServiceLayerClient.BuildContentAddressedQueryCode("PODCRA", "SELECT 2 FROM INV1"));
    }
}
