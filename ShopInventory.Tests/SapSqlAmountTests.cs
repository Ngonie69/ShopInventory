using System.Globalization;
using System.Text.Json;
using ShopInventory.Common.Sap;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Pins how a stored query's decimal is asked for and read back: as text, because a Service Layer
/// worker writing a decimal can stall for good (<see cref="SapSqlAmount"/>). Nothing here talks to SAP.
/// </summary>
public class SapSqlAmountTests
{
    [Fact]
    public void A_decimal_column_is_selected_as_text_under_its_own_name_or_another()
    {
        Assert.Equal("LEFT(T0.\"Quantity\", 40) AS \"Quantity\"", SapSqlAmount.Column("T0", "Quantity"));
        Assert.Equal("LEFT(T1.\"OnHand\", 40) AS \"InStock\"", SapSqlAmount.Column("T1", "OnHand", "InStock"));
        Assert.Equal(
            "LEFT(T1.\"Debit\", 40) AS \"Debit\", LEFT(T1.\"Credit\", 40) AS \"Credit\"",
            SapSqlAmount.Columns("T1", "Debit", "Credit"));
    }

    [Fact]
    public void The_invoice_cost_statement_asks_for_every_amount_as_text_and_leaves_its_where_alone()
    {
        // The statement that left ten Service Layer workers stuck on 23 Nov 2026.
        var sql = SAPServiceLayerClient.InvoiceLineCostSql;

        foreach (var column in new[] { "Quantity", "LineTotal", "TotalFrgn", "GrssProfit", "GrssProfFC" })
        {
            Assert.Contains($"LEFT(T0.\"{column}\", 40) AS \"{column}\"", sql);
        }

        Assert.Contains("WHERE T0.\"DocEntry\" >= :docEntryStart AND T0.\"DocEntry\" <= :docEntryEnd", sql);
        Assert.DoesNotContain("{", sql);
    }

    [Theory]
    [InlineData("\"11069.930000\"", "11069.93")]
    [InlineData("\"-45678.120000\"", "-45678.12")]
    [InlineData("\"0.000000\"", "0")]
    [InlineData("\"5.000000\"", "5")]
    [InlineData("\" 12.500000 \"", "12.5")]
    [InlineData("\"1.5E+2\"", "150")]
    [InlineData("11069.9", "11069.9")]
    [InlineData("42", "42")]
    [InlineData("-0.5", "-0.5")]
    public void An_amount_reads_the_same_as_text_or_as_a_number(string json, string expected)
    {
        var amount = SapSqlAmount.Read(Row($$"""{ "Amount": {{json}} }"""), "Amount");

        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), amount);
        // The scale too: a quantity SAP sent as 5 must not start printing as 5.000000.
        Assert.Equal(expected, amount.ToString(CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("""{ "Amount": null }""")]
    [InlineData("""{ "Amount": "" }""")]
    [InlineData("""{ "Amount": "   " }""")]
    [InlineData("""{ "Other": "1.000000" }""")]
    public void An_amount_with_no_value_is_zero(string json)
    {
        Assert.Equal(0m, SapSqlAmount.Read(Row(json), "Amount"));
        Assert.Null(SapSqlAmount.Parse(Row("""{ "Amount": null }""").GetProperty("Amount"), "Amount"));
    }

    [Theory]
    [InlineData("\"1,234.50\"")]
    [InlineData("\"twelve\"")]
    [InlineData("\"12.50 USD\"")]
    [InlineData("\"1.234,50\"")]
    [InlineData("true")]
    public void Text_that_is_not_a_number_stops_the_read_rather_than_counting_as_zero(string json)
    {
        var ex = Assert.Throws<SapAmountFormatException>(
            () => SapSqlAmount.Read(Row($$"""{ "LineTotal": {{json}} }"""), "LineTotal"));

        Assert.Equal("LineTotal", ex.Column);
        Assert.Contains("LineTotal", ex.Message);
    }

    [Fact]
    public void Text_is_parsed_the_same_whatever_the_servers_culture()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            // A comma decimal separator: "11069.930000" read in this culture is eleven billion.
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

            Assert.Equal(11069.93m, SapSqlAmount.Read(Row("""{ "Amount": "11069.930000" }"""), "Amount"));
            Assert.Equal(11069.93m, SapSqlRow.GetDecimal(new Dictionary<string, object?> { ["Amount"] = "11069.930000" }, "Amount"));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void A_row_from_the_generic_executor_reads_text_whole_numbers_and_decimals()
    {
        var row = new Dictionary<string, object?>
        {
            ["Text"] = "1234567.890000",
            ["Whole"] = 7L,
            ["Fraction"] = 2.5m,
            ["Nothing"] = null,
            ["Words"] = "n/a"
        };

        Assert.Equal(1234567.89m, SapSqlAmount.Read(row, "Text"));
        Assert.Equal(7m, SapSqlAmount.Read(row, "Whole"));
        Assert.Equal(2.5m, SapSqlAmount.Read(row, "Fraction"));
        Assert.Equal(0m, SapSqlAmount.Read(row, "Nothing"));
        Assert.Equal(0m, SapSqlAmount.Read(row, "Missing"));
        Assert.Throws<SapAmountFormatException>(() => SapSqlAmount.Read(row, "Words"));
        // The shared row reader is the same reader: it used to answer zero for text it could not parse.
        Assert.Throws<SapAmountFormatException>(() => SapSqlRow.GetDecimal(row, "Words"));
    }

    [Fact]
    public void An_invoice_cost_line_reads_the_same_whether_its_amounts_come_as_text_or_as_numbers()
    {
        var asText = SAPServiceLayerClient.ReadInvoiceLineCost(new Dictionary<string, object?>
        {
            ["DocEntry"] = 2433442L,
            ["ItemCode"] = "CHE011",
            ["WhsCode"] = "CORMACH2",
            ["Quantity"] = "12.000000",
            ["LineTotal"] = "21336.130000",
            ["TotalFrgn"] = "0.000000",
            ["GrssProfit"] = "4321.870000",
            ["GrssProfFC"] = "0.000000"
        });
        var asNumbers = SAPServiceLayerClient.ReadInvoiceLineCost(new Dictionary<string, object?>
        {
            ["DocEntry"] = 2433442L,
            ["ItemCode"] = "CHE011",
            ["WhsCode"] = "CORMACH2",
            ["Quantity"] = 12L,
            ["LineTotal"] = 21336.13m,
            ["TotalFrgn"] = 0L,
            ["GrssProfit"] = 4321.87m,
            ["GrssProfFC"] = 0L
        });

        Assert.Equal(asNumbers, asText);
        Assert.Equal(21336.13m, asText.Revenue);
        Assert.Equal(4321.87m, asText.GrossProfit);
    }

    [Fact]
    public void An_invoice_cost_line_whose_amount_is_not_a_number_is_not_read_as_a_free_sale()
    {
        var row = new Dictionary<string, object?>
        {
            ["DocEntry"] = 2433442L,
            ["ItemCode"] = "CHE011",
            ["WhsCode"] = "CORMACH2",
            ["Quantity"] = "12.000000",
            ["LineTotal"] = "#####",
            ["TotalFrgn"] = "0.000000",
            ["GrssProfit"] = "4321.870000",
            ["GrssProfFC"] = "0.000000"
        };

        Assert.Throws<SapAmountFormatException>(() => SAPServiceLayerClient.ReadInvoiceLineCost(row));
    }

    private static JsonElement Row(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
