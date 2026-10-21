using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Covers the rules behind the Create Invoice page's fast-entry layout: when a line counts as
/// short of stock (and so blocks the post), which currency a customer fixes, what a line is worth,
/// and how the recent customers lead the picker.
/// </summary>
public sealed class InvoiceEntryTests
{
    [Fact]
    public void A_line_asking_for_more_than_is_in_stock_is_short_by_the_difference()
    {
        var stock = InvoiceEntry.EvaluateStock(quantity: 120m, inStock: 84m, loading: false);

        Assert.Equal(LineStockState.Short, stock.State);
        Assert.True(stock.IsShort);
        Assert.Equal(36m, stock.ShortBy);
    }

    [Fact]
    public void A_line_taking_exactly_what_is_in_stock_is_not_short()
    {
        var stock = InvoiceEntry.EvaluateStock(40m, 40m, false);

        Assert.False(stock.IsShort);
        Assert.Equal(LineStockState.Low, stock.State);
    }

    [Fact]
    public void A_line_well_within_stock_is_enough()
    {
        Assert.Equal(LineStockState.Enough, InvoiceEntry.EvaluateStock(24m, 400m, false).State);
    }

    [Fact]
    public void Oversold_stock_leaves_the_whole_line_short()
    {
        // OnHand can read negative when a warehouse is oversold.
        var stock = InvoiceEntry.EvaluateStock(10m, -3m, false);

        Assert.True(stock.IsShort);
        Assert.Equal(10m, stock.ShortBy);
    }

    [Theory]
    [InlineData(false, LineStockState.Unknown)]
    [InlineData(true, LineStockState.Loading)]
    public void A_missing_figure_never_reads_as_short(bool loading, LineStockState expected)
    {
        // A failed read and an item with no warehouse row look the same; neither may block a sale.
        var stock = InvoiceEntry.EvaluateStock(500m, null, loading);

        Assert.Equal(expected, stock.State);
        Assert.False(stock.IsShort);
    }

    [Fact]
    public void A_zero_quantity_is_left_to_its_own_error()
    {
        Assert.False(InvoiceEntry.EvaluateStock(0m, 0m, false).IsShort);
    }

    [Theory]
    [InlineData("USD", "USD")]
    [InlineData(" ZIG ", "ZIG")]
    [InlineData("##", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Only_a_real_currency_is_fixed_by_the_customer(string? partnerCurrency, string? expected)
    {
        Assert.Equal(expected, InvoiceEntry.DocumentCurrency(partnerCurrency));
    }

    [Fact]
    public void A_line_is_worth_its_price_less_its_discount()
    {
        Assert.Equal(48m, InvoiceEntry.LineNet(10m, 5m, 4m));
        Assert.Equal(50m, InvoiceEntry.LineNet(10m, 5m, null));
        Assert.Equal(0m, InvoiceEntry.LineNet(10m, null, 4m));
    }

    [Fact]
    public void Invoicing_a_customer_moves_them_to_the_front_of_the_recent_list()
    {
        var recent = InvoiceEntry.PushRecent(["A1", "B2", "C3"], "c3");

        Assert.Equal(["c3", "A1", "B2"], recent);
    }

    [Fact]
    public void The_recent_list_is_capped()
    {
        var recent = InvoiceEntry.PushRecent(["A", "B", "C", "D", "E"], "F");

        Assert.Equal(InvoiceEntry.RecentCustomerLimit, recent.Count);
        Assert.Equal("F", recent[0]);
        Assert.DoesNotContain("E", recent);
    }

    [Fact]
    public void Recent_customers_lead_the_picker_once_each_and_marked()
    {
        var partners = new[]
        {
            Partner("C001", "Avondale Fresh Market", "USD"),
            Partner("C002", "Belvedere Corner Shop", "##"),
            Partner("C003", "Greenfield Supermarket", "ZIG"),
        };

        var options = InvoiceEntry.CustomerOptions(partners, ["C003", "C002", "GONE"]);

        Assert.Equal(["C003", "C002", "C001"], options.Select(o => o.Value));
        Assert.Equal("Recent · ZIG", options[0].Hint);
        Assert.Equal("Recent", options[1].Hint);
        Assert.Equal("USD", options[2].Hint);
    }

    [Fact]
    public void Without_recent_customers_the_picker_is_by_code()
    {
        var options = InvoiceEntry.CustomerOptions(
            [Partner("C2", "B", "USD"), Partner("C1", "A", "USD")], []);

        Assert.Equal(["C1", "C2"], options.Select(o => o.Value));
    }

    private static BusinessPartnerDto Partner(string code, string name, string currency) =>
        new() { CardCode = code, CardName = name, Currency = currency, IsActive = true };
}
