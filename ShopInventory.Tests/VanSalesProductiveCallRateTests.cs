using ShopInventory.Features.VanSalesReports.Queries;

namespace ShopInventory.Tests;

/// <summary>
/// Pins the PCR's two halves. The routes table once printed 1,700% for a route with 34 buying shops
/// over 2 check-ins: every day's buyers on top, only the checked-in days' check-ins beneath, and a shop
/// that bought without a check-in counted as a productive call that was never a call at all.
/// </summary>
public sealed class VanSalesProductiveCallRateTests
{
    private static readonly Guid Rep = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly DateTime Monday = new(2026, 9, 28);
    private static readonly DateTime Tuesday = new(2026, 9, 29);

    [Fact]
    public void A_shop_that_bought_without_a_check_in_is_a_call()
    {
        Assert.Equal(3, VanSalesMeasures.CountCallsMade(["A", "B"], ["B", "C"], hasUnattributedSale: false));
    }

    [Fact]
    public void Check_ins_and_sales_match_on_code_regardless_of_case()
    {
        Assert.Equal(1, VanSalesMeasures.CountCallsMade(["tuck01"], ["TUCK01"], hasUnattributedSale: false));
    }

    /// <summary>
    /// A van selling to real business partners puts no shop on the sale, while its check-ins still
    /// carry one. The unattributed sale is one of those visits, not an extra door.
    /// </summary>
    [Fact]
    public void An_unattributed_sale_is_taken_as_one_of_the_shops_checked_into()
    {
        Assert.Equal(2, VanSalesMeasures.CountCallsMade(["A", "B"], [], hasUnattributedSale: true));
    }

    [Fact]
    public void An_unattributed_sale_adds_a_call_once_every_check_in_is_a_named_buyer()
    {
        Assert.Equal(2, VanSalesMeasures.CountCallsMade(["A"], ["A"], hasUnattributedSale: true));
    }

    [Fact]
    public void Sales_on_a_day_with_no_check_ins_stay_out_of_both_halves()
    {
        var facts = new[]
        {
            Sale(Monday, "A"),
            Sale(Tuesday, "B"), Sale(Tuesday, "C"), Sale(Tuesday, "D"), Sale(Tuesday, "E")
        };
        var visits = new Dictionary<VanSalesDayKey, HashSet<string>>
        {
            [new(Rep, Monday)] = new(StringComparer.OrdinalIgnoreCase) { "A", "Z" }
        };

        var basis = VanSalesMeasures.MeasureProductiveCalls(facts.Select(f => f.Key), facts, visits);

        Assert.NotNull(basis);
        Assert.Equal(1, basis.ProductiveCalls);
        Assert.Equal(2, basis.Calls);
    }

    [Fact]
    public void The_rate_cannot_pass_one_however_few_check_ins_were_recorded()
    {
        // The screenshot's shape: a handful of check-ins against a day of selling.
        var facts = Enumerable.Range(1, 17).Select(i => Sale(Monday, $"SHOP{i}")).ToList();
        var visits = new Dictionary<VanSalesDayKey, HashSet<string>>
        {
            [new(Rep, Monday)] = new(StringComparer.OrdinalIgnoreCase) { "SHOP1", "OTHER" }
        };

        var basis = VanSalesMeasures.MeasureProductiveCalls([new(Rep, Monday)], facts, visits)!;

        Assert.Equal(17, basis.ProductiveCalls);
        Assert.Equal(18, basis.Calls);
    }

    [Fact]
    public void No_day_with_check_ins_means_no_basis_rather_than_zero()
    {
        var facts = new[] { Sale(Monday, "A") };

        Assert.Null(VanSalesMeasures.MeasureProductiveCalls(
            [new(Rep, Monday)], facts, new Dictionary<VanSalesDayKey, HashSet<string>>()));
    }

    [Fact]
    public void A_day_of_check_ins_with_no_sales_is_measured_as_unproductive()
    {
        var visits = new Dictionary<VanSalesDayKey, HashSet<string>>
        {
            [new(Rep, Monday)] = new(StringComparer.OrdinalIgnoreCase) { "A", "B", "C" }
        };

        var basis = VanSalesMeasures.MeasureProductiveCalls([new(Rep, Monday)], [], visits)!;

        Assert.Equal(0, basis.ProductiveCalls);
        Assert.Equal(3, basis.Calls);
    }

    private static VanSaleFact Sale(DateTime day, string? shop) => new(
        UserId: Rep,
        TradingDate: day,
        Source: VanSaleSource.OfflineBatch,
        ExternalReferenceId: Guid.NewGuid().ToString(),
        VanAccountCode: "VAN010",
        RouteCustomerId: null,
        RouteCustomerCode: shop,
        RouteCustomerName: shop,
        PaymentMethod: "Cash",
        TotalAmount: 10m,
        Currency: "USD",
        WarehouseCode: "VAN010");
}
