using ShopInventory.Web.Components;
using ShopInventory.Web.Models;

namespace ShopInventory.Tests;

/// <summary>
/// SAP books gross profit on the value before VAT, while the takings the reports print beside it carry
/// VAT. These pin the wording that states the before-VAT base under every profit figure.
/// </summary>
public class GrossProfitFormatTests
{
    [Fact]
    public void The_base_is_stated_before_VAT()
    {
        Assert.Equal("on 1,234.50 excl. VAT", GrossProfitFormat.Base(1234.5m));
    }

    [Fact]
    public void A_partly_costed_row_states_its_base_and_how_much_of_it_is_in_SAP()
    {
        Assert.Equal(
            "on 400.00 excl. VAT · 12 of 15 sales",
            GrossProfitFormat.Base(400m, GrossProfitFormat.PartOf(12, 15)));
    }

    [Fact]
    public void Without_a_base_only_the_part_is_stated()
    {
        Assert.Equal("on 12 of 15 sales", GrossProfitFormat.Base(null, GrossProfitFormat.PartOf(12, 15)));
        Assert.Null(GrossProfitFormat.Base(null));
    }

    [Fact]
    public void The_tooltip_line_names_the_base_when_given()
    {
        Assert.Equal("GP 42.00 · 28.0% margin on 150.00 excl. VAT", GrossProfitFormat.Line(42m, 28m, 150m));
        Assert.Equal("GP 42.00 · 28.0% margin", GrossProfitFormat.Line(42m, 28m));
    }

    [Fact]
    public void Van_and_route_figures_carry_the_costed_value_before_VAT()
    {
        var van = GrossProfitFormat.Of([new VanSalesMoney { Currency = "USD", Gross = 115m, GrossProfit = 25m, CostedNetAmount = 100m }]);
        Assert.Equal(100m, Assert.Single(van).CostedNet);

        var route = GrossProfitFormat.Of([new RouteCustomerSalesTotalsModel { Currency = "USD", Gross = 115m, GrossProfit = 25m, CostedNetAmount = 100m }]);
        Assert.Equal(100m, Assert.Single(route).CostedNet);
    }
}
