using ShopInventory.Web.Services;
using Xunit;

namespace ShopInventory.Tests;

/// <summary>
/// CSV escaping, shared by the audit trail export and the fiscalisation console's two.
/// </summary>
/// <remarks>
/// A fiscal export that is quietly one column out is worse than no export, because it still opens. The
/// three characters that do that are all ordinary in this data: a comma in a customer name, a quote or a
/// newline in a platform error message.
/// </remarks>
public sealed class CsvFileTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Nothing_escapes_to_nothing(string? value)
    {
        Assert.Equal(string.Empty, CsvFile.Escape(value));
    }

    [Fact]
    public void A_plain_field_is_left_alone()
    {
        Assert.Equal("Cybrex Private Limited", CsvFile.Escape("Cybrex Private Limited"));
    }

    [Fact]
    public void A_comma_is_quoted()
    {
        Assert.Equal(
            "\"Moreton Enterprises (Pvt) Ltd, t/a Billy's Meats\"",
            CsvFile.Escape("Moreton Enterprises (Pvt) Ltd, t/a Billy's Meats"));
    }

    [Fact]
    public void A_quote_is_doubled_and_the_field_quoted()
    {
        Assert.Equal(
            "\"ZIMRA said \"\"fiscal day already closed\"\"\"",
            CsvFile.Escape("ZIMRA said \"fiscal day already closed\""));
    }

    [Theory]
    [InlineData("line one\r\nline two")]
    [InlineData("line one\rline two")]
    [InlineData("line one\nline two")]
    public void Every_line_ending_becomes_one_newline_inside_one_quoted_field(string value)
    {
        // Normalised first so what a reader sees is the field this method decided to quote, not whatever
        // line ending the platform's error happened to carry.
        Assert.Equal("\"line one\nline two\"", CsvFile.Escape(value));
    }

    [Fact]
    public void A_row_escapes_each_field_and_joins_them()
    {
        var row = CsvFile.Row("772575", "Pick n Pay, Avondale", null, "34.65");

        Assert.Equal("772575,\"Pick n Pay, Avondale\",,34.65", row);
    }
}
