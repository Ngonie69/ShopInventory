using ShopInventory.Services;

namespace ShopInventory.Tests;

public class SapSqlTextTests
{
    [Fact]
    public void Trailing_semicolon_is_stripped()
    {
        // SAP's SQLQueries endpoint rejects the terminator outright: "Invalid SQL syntax ...
        // Incorrect syntax near ';'". One of these broke customer statements completely.
        Assert.Equal(
            "SELECT 1 FROM OJDT",
            SAPServiceLayerClient.NormalizeSapSqlText("SELECT 1 FROM OJDT;"));
    }

    [Fact]
    public void Trailing_semicolon_after_a_newline_is_stripped()
    {
        // The real case: SQL built with a verbatim string, terminator on the last line.
        Assert.Equal(
            "SELECT 1\nFROM OJDT\nORDER BY \"RefDate\"",
            SAPServiceLayerClient.NormalizeSapSqlText("SELECT 1\nFROM OJDT\nORDER BY \"RefDate\";\n  "));
    }

    [Fact]
    public void Repeated_terminators_and_the_space_before_them_are_removed()
    {
        Assert.Equal(
            "SELECT 1",
            SAPServiceLayerClient.NormalizeSapSqlText("SELECT 1 ; ;"));
    }

    [Fact]
    public void Semicolons_inside_the_statement_are_left_alone()
    {
        // Only the terminator is meaningless to SAP. A semicolon inside a literal is data.
        const string sql = "SELECT 1 FROM OJDT WHERE \"Memo\" = 'a;b'";

        Assert.Equal(sql, SAPServiceLayerClient.NormalizeSapSqlText(sql));
    }

    [Fact]
    public void Statement_without_a_terminator_is_unchanged()
    {
        const string sql = "SELECT 1 FROM OJDT";

        Assert.Equal(sql, SAPServiceLayerClient.NormalizeSapSqlText(sql));
    }

    /// <summary>
    /// Covers the query string that binds <c>:name</c> parameters on
    /// <c>SQLQueries('code')/List</c>. This is the piece that lets the statement queries keep one
    /// fixed SQL text instead of minting a SAP-side object per request.
    /// </summary>
    [Fact]
    public void A_parameterless_query_gets_no_parameter_string()
    {
        Assert.Equal(string.Empty, SAPServiceLayerClient.BuildSqlParameterQueryString(null));
        Assert.Equal(string.Empty, SAPServiceLayerClient.BuildSqlParameterQueryString(new Dictionary<string, string>()));
    }

    [Fact]
    public void Parameters_are_quoted_and_end_with_a_separator_for_the_caller_s_odata_options()
    {
        // SAP rejects a bare value with "Parameter error", so the quotes travel as part of the
        // encoded value. The trailing & is what lets the caller append $skip.
        var query = SAPServiceLayerClient.BuildSqlParameterQueryString(
            new Dictionary<string, string> { ["cardCode"] = "ABS006" });

        Assert.Equal("cardCode=%27ABS006%27&", query);
    }

    [Fact]
    public void A_quote_in_a_value_is_doubled_so_it_cannot_end_the_literal_early()
    {
        // A card code like O'Brien would otherwise terminate the SQL literal, which is both a
        // correctness bug and the shape of an injection.
        var query = SAPServiceLayerClient.BuildSqlParameterQueryString(
            new Dictionary<string, string> { ["cardCode"] = "O'Brien" });

        Assert.Equal("cardCode=%27O%27%27Brien%27&", query);
        Assert.Equal("'O''Brien'", Uri.UnescapeDataString(query.Split('=')[1].TrimEnd('&')));
    }

    [Fact]
    public void Several_parameters_are_joined_for_sap()
    {
        var query = SAPServiceLayerClient.BuildSqlParameterQueryString(
            new Dictionary<string, string>
            {
                ["cardCode"] = "ABS006",
                ["fromDate"] = "2026-05-01",
                ["toDate"] = "2026-05-31"
            });

        Assert.Equal("cardCode=%27ABS006%27&fromDate=%272026-05-01%27&toDate=%272026-05-31%27&", query);
    }
}
