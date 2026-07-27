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
