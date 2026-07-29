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
}
