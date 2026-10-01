using ShopInventory.Features.VanSalesReports.Queries;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// Pins the PCR's two halves. The routes table once printed 1,700% for a route with 34 buying shops
/// over 2 check-ins: every day's buyers on top, only the checked-in days' check-ins beneath, and a shop
/// that bought without a check-in counted as a productive call that was never a call at all.
///
/// Each truck carries two reps under one van account who take turns at the counter, so the rates
/// are measured per truck-day, pooled across both. See <see cref="VanTruckDays"/>.
/// </summary>
public sealed class VanSalesProductiveCallRateTests
{
    private static readonly Guid Rep = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid Mate = Guid.Parse("55555555-5555-5555-5555-555555555555");
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
            Sale(Rep, Monday, "A"),
            Sale(Rep, Tuesday, "B"), Sale(Rep, Tuesday, "C"), Sale(Rep, Tuesday, "D"), Sale(Rep, Tuesday, "E")
        };
        var ledger = Ledger(facts, Visits((Rep, Monday, ["A", "Z"])));

        var basis = ledger.MeasureProductiveCalls(facts.Select(f => f.Key))!;

        Assert.Equal(1, basis.ProductiveCalls);
        Assert.Equal(2, basis.Calls);
    }

    [Fact]
    public void The_rate_cannot_pass_one_however_few_check_ins_were_recorded()
    {
        // The screenshot's shape: a handful of check-ins against a day of selling.
        var facts = Enumerable.Range(1, 17).Select(i => Sale(Rep, Monday, $"SHOP{i}")).ToList();
        var ledger = Ledger(facts, Visits((Rep, Monday, ["SHOP1", "OTHER"])));

        var basis = ledger.MeasureProductiveCalls([new(Rep, Monday)])!;

        Assert.Equal(17, basis.ProductiveCalls);
        Assert.Equal(18, basis.Calls);
    }

    [Fact]
    public void No_day_with_check_ins_means_no_basis_rather_than_zero()
    {
        var facts = new[] { Sale(Rep, Monday, "A") };

        Assert.Null(Ledger(facts, Visits()).MeasureProductiveCalls([new(Rep, Monday)]));
    }

    [Fact]
    public void A_day_of_check_ins_with_no_sales_is_measured_as_unproductive()
    {
        var ledger = Ledger([], Visits((Rep, Monday, ["A", "B", "C"])));

        var basis = ledger.MeasureProductiveCalls([new(Rep, Monday)])!;

        Assert.Equal(0, basis.ProductiveCalls);
        Assert.Equal(3, basis.Calls);
    }

    // --- Two reps on one truck ---

    /// <summary>
    /// One rep checks in and the other writes the invoice. That is one call that bought, not a
    /// check-in that bought nothing plus a sale on a day with no calls.
    /// </summary>
    [Fact]
    public void A_check_in_by_one_rep_and_a_sale_by_the_other_is_one_productive_call()
    {
        var facts = new[] { Sale(Mate, Monday, "A") };
        var ledger = Ledger(facts, Visits((Rep, Monday, ["A", "B"])), shared: true);

        foreach (var who in new[] { Rep, Mate })
        {
            var basis = ledger.MeasureProductiveCalls([new(who, Monday)])!;
            Assert.Equal(1, basis.ProductiveCalls);
            Assert.Equal(2, basis.Calls);
        }
    }

    [Fact]
    public void A_shop_both_reps_checked_into_is_one_call()
    {
        var ledger = Ledger([], Visits((Rep, Monday, ["A", "B"]), (Mate, Monday, ["a", "C"])), shared: true);

        Assert.Equal(3, ledger.CountCalls([new(Rep, Monday), new(Mate, Monday)]));
    }

    [Fact]
    public void A_plan_both_reps_snapshotted_counts_once()
    {
        var ledger = Ledger([], Visits((Rep, Monday, ["A"])), shared: true,
            routeDays: [RouteDay(Rep, Monday, 30), RouteDay(Mate, Monday, 30)]);

        Assert.Equal(30, ledger.SumPlanned([new(Rep, Monday), new(Mate, Monday)]));
        Assert.Equal((30, 1), ledger.MeasureAgainstPlan([new(Rep, Monday), new(Mate, Monday)]));
    }

    [Fact]
    public void The_rep_who_did_not_open_the_day_still_reads_the_trucks_plan()
    {
        var ledger = Ledger([], Visits((Mate, Monday, ["A"])), shared: true,
            routeDays: [RouteDay(Rep, Monday, 30)]);

        Assert.Equal(30, ledger.SumPlanned([new(Mate, Monday)]));
    }

    [Fact]
    public void Reps_on_different_trucks_are_never_pooled()
    {
        var facts = new[] { Sale(Mate, Monday, "A") };
        var ledger = Ledger(facts, Visits((Rep, Monday, ["A"])), shared: false);

        var basis = ledger.MeasureProductiveCalls([new(Rep, Monday)])!;
        Assert.Equal(0, basis.ProductiveCalls);
        Assert.Equal(1, basis.Calls);
        Assert.Null(ledger.MeasureProductiveCalls([new(Mate, Monday)]));
    }

    [Fact]
    public void Two_rep_days_on_one_truck_day_are_measured_once()
    {
        var facts = new[] { Sale(Rep, Monday, "A"), Sale(Mate, Monday, "B") };
        var ledger = Ledger(facts, Visits((Rep, Monday, ["A", "B", "C"])), shared: true);

        var basis = ledger.MeasureProductiveCalls([new(Rep, Monday), new(Mate, Monday)])!;

        Assert.Equal(2, basis.ProductiveCalls);
        Assert.Equal(3, basis.Calls);
    }

    /// <summary>Both reps selling to one shop on one day is one call that bought, not two.</summary>
    [Fact]
    public void A_shop_both_reps_sold_to_is_one_productive_call()
    {
        var facts = new[] { Sale(Rep, Monday, "A"), Sale(Mate, Monday, "a"), Sale(Rep, Monday, null), Sale(Mate, Monday, null) };
        var ledger = Ledger(facts, Visits((Rep, Monday, ["A", "B"])), shared: true);

        var basis = ledger.MeasureProductiveCalls([new(Rep, Monday)])!;

        Assert.Equal(2, basis.ProductiveCalls);
        Assert.Equal(2, basis.Calls);
    }

    private static VanTruckDays Ledger(
        IEnumerable<VanSaleFact> facts,
        Dictionary<VanSalesDayKey, HashSet<string>> visits,
        bool shared = false,
        IEnumerable<VanRouteDayEntity>? routeDays = null) =>
        VanTruckDays.Build(
            facts,
            visits,
            routeDays ?? [],
            new Dictionary<Guid, string?> { [Rep] = "VAN010", [Mate] = shared ? "van010 " : "VAN011" });

    private static Dictionary<VanSalesDayKey, HashSet<string>> Visits(
        params (Guid Who, DateTime Day, string[] Shops)[] checkIns) =>
        checkIns.ToDictionary(
            c => new VanSalesDayKey(c.Who, c.Day),
            c => c.Shops.ToHashSet(StringComparer.OrdinalIgnoreCase));

    private static VanRouteDayEntity RouteDay(Guid who, DateTime day, int planned) => new()
    {
        UserId = who,
        Username = who.ToString(),
        TradingDate = day,
        DepartedAt = day.AddHours(5),
        PlannedCustomerCount = planned
    };

    private static VanSaleFact Sale(Guid who, DateTime day, string? shop) => new(
        UserId: who,
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
