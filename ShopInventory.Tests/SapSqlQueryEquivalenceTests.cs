using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Guards the check that decides whether a stored SAP SQL query still matches the statement about
/// to be run, and the fingerprint that check must not disturb.
/// </summary>
/// <remarks>
/// SAP strips whitespace from the end of each line it stores. Four statements in this codebase were
/// written with a line ending in a space, so the stored text never compared equal to the text being
/// sent: every probe logged "exists with different text" and PATCHed a query nobody had edited —
/// a write against an OUQR that is already oversized, once per query per verification lifetime.
/// Nothing about it was visible from outside; the queries returned exactly the right rows.
/// </remarks>
public class SapSqlQueryEquivalenceTests
{
    private const string Statement = "SELECT\n    T0.\"ListNum\",\n    T0.\"ListName\"\nFROM OPLN T0";

    [Fact]
    public void A_statement_matches_itself_after_SAP_strips_line_end_whitespace()
    {
        var written = "SELECT \n    T0.\"ListNum\", \n    T0.\"ListName\"\nFROM OPLN T0";
        var storedBySap = Statement;

        Assert.True(SAPServiceLayerClient.SqlTextsAreEquivalent(storedBySap, written));
    }

    [Fact]
    public void A_statement_matches_itself_after_SAP_rewrites_newlines_as_bare_CR()
    {
        // SAP returns CRLF-posted text with bare CR; the caller's literal keeps whatever the source
        // file uses.
        var storedBySap = Statement.Replace("\n", "\r");

        Assert.True(SAPServiceLayerClient.SqlTextsAreEquivalent(storedBySap, Statement));
        Assert.True(SAPServiceLayerClient.SqlTextsAreEquivalent(storedBySap, Statement.Replace("\n", "\r\n")));
    }

    [Fact]
    public void A_trailing_semicolon_does_not_count_as_a_difference()
    {
        // The create path strips it before sending, so the caller's copy keeps one SAP never saw.
        Assert.True(SAPServiceLayerClient.SqlTextsAreEquivalent(Statement, Statement + ";"));
    }

    /// <summary>
    /// SAP quotes a bare table name in the statement it stores. This was the whole reason every
    /// query code re-PATCHed once an hour, and none of the other transforms could see it: the
    /// statement below is one line, with no trailing whitespace and no newlines to normalise.
    /// </summary>
    /// <remarks>
    /// Both strings are verbatim from production (KEFALOS_USD_NEW2) on 2026-08-07 — the ORD_FULF_CUST
    /// statement as ReportService builds it for the company-wide report, and the SqlText SAP handed
    /// back for that code.
    /// </remarks>
    [Fact]
    public void A_statement_matches_itself_after_SAP_quotes_the_table_name()
    {
        const string written =
            "SELECT T0.\"CardCode\", T0.\"CardName\" FROM ORDR T0 WHERE T0.\"DocDate\" >= :fromDate";
        const string storedBySap =
            "SELECT T0.\"CardCode\", T0.\"CardName\" FROM \"ORDR\" T0 WHERE T0.\"DocDate\" >= :fromDate";

        Assert.True(SAPServiceLayerClient.SqlTextsAreEquivalent(storedBySap, written));
    }

    [Fact]
    public void Every_join_in_a_statement_is_matched_the_same_way()
    {
        // ORD_FULF_LINE, the statement the report died on. SAP quoted both tables.
        var written = "SELECT T1.\"LineNum\"\nFROM ORDR T0\nINNER JOIN RDR1 T1 ON T0.\"DocEntry\" = T1.\"DocEntry\"";
        var storedBySap = written
            .Replace("FROM ORDR", "FROM \"ORDR\"", StringComparison.Ordinal)
            .Replace("JOIN RDR1", "JOIN \"RDR1\"", StringComparison.Ordinal);

        Assert.True(SAPServiceLayerClient.SqlTextsAreEquivalent(storedBySap, written));
    }

    [Fact]
    public void A_statement_that_already_quotes_its_tables_still_matches()
    {
        // Several statements here are written with the tables quoted, so SAP stores them unchanged.
        // Collapsing both sides has to leave those matching, not break them.
        const string statement = "SELECT T0.\"ItemCode\" FROM \"ITM1\" T0 INNER JOIN \"OITM\" T1 ON T0.\"ItemCode\" = T1.\"ItemCode\"";

        Assert.True(SAPServiceLayerClient.SqlTextsAreEquivalent(statement, statement));
    }

    /// <summary>
    /// The collapse is safe only because HANA folds an unquoted identifier to upper case, so
    /// <c>"ORDR"</c> and <c>ORDR</c> are one table. A mixed-case quoted name is a different table
    /// from the unquoted spelling, and has to keep reading as a difference.
    /// </summary>
    [Fact]
    public void A_mixed_case_quoted_table_is_not_collapsed()
    {
        const string quoted = "SELECT * FROM \"MyTable\" T0";
        const string bare = "SELECT * FROM MyTable T0";

        Assert.False(SAPServiceLayerClient.SqlTextsAreEquivalent(quoted, bare));
    }

    [Fact]
    public void Quoting_elsewhere_in_the_statement_is_untouched()
    {
        // Only the identifier directly after FROM/JOIN is collapsed. A column or alias that differs
        // by quoting is a real edit — it changes what the row is called on the way out.
        const string quotedColumn = "SELECT T0.\"DocNum\" AS \"DOCNUM\" FROM ORDR T0";
        const string bareColumn = "SELECT T0.\"DocNum\" AS DOCNUM FROM ORDR T0";

        Assert.False(SAPServiceLayerClient.SqlTextsAreEquivalent(quotedColumn, bareColumn));
    }

    [Fact]
    public void A_genuinely_edited_statement_is_still_a_difference()
    {
        var edited = Statement.Replace("T0.\"ListName\"", "T0.\"ListName\", T0.\"Factor\"");

        Assert.False(SAPServiceLayerClient.SqlTextsAreEquivalent(Statement, edited));
    }

    [Fact]
    public void Whitespace_inside_a_line_still_counts_as_a_difference()
    {
        // Runs inside a line can sit inside a string literal, where collapsing them would change
        // what the statement selects. Only end-of-line whitespace is ignored.
        var widened = Statement.Replace("FROM OPLN T0", "FROM  OPLN T0");

        Assert.False(SAPServiceLayerClient.SqlTextsAreEquivalent(Statement, widened));
    }

    /// <summary>
    /// The equivalence check is deliberately looser than the fingerprint. Loosening the fingerprint
    /// instead would move every content-addressed query code and mint a fresh SQLQueries row for
    /// each, orphaning the existing rows.
    /// </summary>
    [Fact]
    public void Line_end_whitespace_still_changes_the_normalised_text_the_fingerprint_is_built_from()
    {
        var written = "SELECT \n    T0.\"ListNum\"";
        var storedBySap = "SELECT\n    T0.\"ListNum\"";

        Assert.NotEqual(
            SAPServiceLayerClient.NormalizeSqlText(storedBySap),
            SAPServiceLayerClient.NormalizeSqlText(written));
    }

    [Fact]
    public void Content_addressed_codes_are_unchanged_by_the_looser_comparison()
    {
        // Pins the codes seen in production so a future change to NormalizeSqlText cannot silently
        // re-mint them. These are the price-list-35 statement's real code and its POD equivalent.
        Assert.Equal(
            "SH_PL35_1239DBF4EC45",
            SAPServiceLayerClient.BuildContentAddressedQueryCode("SH_PL35", PriceList35Statement));
    }

    private const string PriceList35Statement = """

        SELECT T0."ItemCode",
               T1."ItemName",
               T1."FrgnName",
               P."ListNum" AS "PriceList",
               T0."Price",
               T0."Currency",
               P."ListName",
               P."BASE_NUM" AS "BasePriceList",
               P."Factor"
        FROM "ITM1" T0
        INNER JOIN "OITM" T1 ON T0."ItemCode" = T1."ItemCode"
        INNER JOIN "OPLN" P ON P."ListNum" = T0."PriceList"
        WHERE T0."PriceList" = 35
          AND T0."Price" > 0
        ORDER BY T0."ItemCode"
        """;
}
