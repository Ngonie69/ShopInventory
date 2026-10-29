using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The route checklist's run view: each route shows its run on or after the chosen day, ticked by the
/// orders placed for that run. The cases are the 27 Oct 2026 checklist that read Borrowdale as
/// unordered on the Tuesday its truck went out, with the routes from the generated catalogue.
/// </summary>
public sealed class RouteChecklistRunTests
{
    private static readonly DateTime Tuesday27 = new(2026, 10, 27);
    private static int _nextId = 1;

    /// <summary>An order taken at a UTC instant, as the API sends it.</summary>
    private static SalesOrderDto Order(
        string cardCode,
        DateTime placedUtc,
        DateTime? delivery = null,
        SalesOrderStatus status = SalesOrderStatus.Approved) => new()
    {
        Id = _nextId++,
        OrderNumber = $"SO-RUN-{_nextId}",
        CardCode = cardCode,
        CardName = cardCode,
        Status = status,
        OrderDate = DateTime.SpecifyKind(placedUtc, DateTimeKind.Utc),
        DeliveryDate = delivery is { } date ? DateTime.SpecifyKind(date, DateTimeKind.Utc) : null
    };

    private static RouteChecklist Runs(DateTime day, params SalesOrderDto[] orders) =>
        RouteOrderChecklist.BuildForRuns(DeliveryRouteDirectory.Build([]), orders, day);

    private static RouteChecklistRoute RouteOf(RouteChecklist checklist, string name) =>
        checklist.Routes.Single(route => route.Route.Name == name);

    private static RouteChecklistShop ShopOf(RouteChecklist checklist, string route, string cardCode) =>
        RouteOf(checklist, route).Shops.Single(shop => shop.CardCodes.Contains(cardCode));

    [Fact]
    public void A_Monday_order_ticks_the_shop_on_its_Tuesday_run()
    {
        // SO 85618: Spar Borrowdale Village ordered on Monday 26 Oct and was invoiced on the Tuesday.
        var order = Order("SPA053 USD", new DateTime(2026, 10, 26, 14, 0, 0), delivery: new DateTime(2026, 10, 28));

        var checklist = Runs(Tuesday27, order);

        var borrowdale = RouteOf(checklist, "BORROWDALE");
        Assert.Equal(Tuesday27, borrowdale.RunDate);
        Assert.Equal([order], ShopOf(checklist, "BORROWDALE", "SPA053 USD").Orders);
        Assert.Equal(1, checklist.OrderCount);
    }

    [Fact]
    public void Last_weeks_order_does_not_tick_this_weeks_run()
    {
        var lastMonday = Order("SPA053 USD", new DateTime(2026, 10, 19, 14, 0, 0));

        Assert.False(ShopOf(Runs(Tuesday27, lastMonday), "BORROWDALE", "SPA053 USD").HasOrdered);
    }

    [Fact]
    public void An_order_placed_on_the_run_day_counts_for_that_run()
    {
        // TM Sakubva on Marondera ordered at 02:09 on Tuesday 27 Oct and was invoiced that day, though
        // the handset gave it a delivery date of the next Tuesday.
        var order = Order("TMP108", new DateTime(2026, 10, 27, 0, 9, 0), delivery: new DateTime(2026, 11, 3));

        Assert.True(ShopOf(Runs(Tuesday27, order), "MARONDERA-CHIPINGE", "TMP108").HasOrdered);
        Assert.False(ShopOf(Runs(new DateTime(2026, 11, 3), order), "MARONDERA-CHIPINGE", "TMP108").HasOrdered);
    }

    [Fact]
    public void An_order_is_placed_on_its_Harare_day()
    {
        // 22:30 UTC on Tuesday is 00:30 on Wednesday in Harare, after Tuesday's run: it is for the next.
        var order = Order("SPA053 USD", new DateTime(2026, 10, 27, 22, 30, 0));

        Assert.False(ShopOf(Runs(Tuesday27, order), "BORROWDALE", "SPA053 USD").HasOrdered);
        Assert.True(ShopOf(Runs(new DateTime(2026, 11, 3), order), "BORROWDALE", "SPA053 USD").HasOrdered);
    }

    [Fact]
    public void On_a_day_a_route_does_not_run_it_shows_its_next_run()
    {
        var monday = Order("SPA053 USD", new DateTime(2026, 10, 26, 14, 0, 0));
        var wednesday = Order("SPA053 USD", new DateTime(2026, 10, 28, 9, 0, 0));

        var checklist = Runs(new DateTime(2026, 10, 28), monday, wednesday);

        Assert.Equal(new DateTime(2026, 11, 3), RouteOf(checklist, "BORROWDALE").RunDate);
        Assert.Equal([wednesday], ShopOf(checklist, "BORROWDALE", "SPA053 USD").Orders);
    }

    [Fact]
    public void A_route_that_runs_twice_a_week_counts_each_run_apart()
    {
        // Bulawayo runs on Mondays and Fridays: a Tuesday order is for the Friday.
        var tuesday = Order("CHE020", new DateTime(2026, 10, 27, 9, 0, 0));

        var friday = Runs(new DateTime(2026, 10, 30), tuesday);
        var monday = Runs(new DateTime(2026, 10, 26), tuesday);

        Assert.Equal(new DateTime(2026, 10, 30), RouteOf(friday, "BULAWAYO").RunDate);
        Assert.True(ShopOf(friday, "BULAWAYO", "CHE020").HasOrdered);
        Assert.False(ShopOf(monday, "BULAWAYO", "CHE020").HasOrdered);
    }

    [Fact]
    public void Shops_on_no_route_show_the_orders_placed_that_day()
    {
        var today = Order("GRE011", new DateTime(2026, 10, 27, 7, 30, 0));
        var yesterday = Order("GRE011", new DateTime(2026, 10, 26, 7, 30, 0));

        Assert.Equal([today], Runs(Tuesday27, today, yesterday).OffRouteOrders);
    }

    [Fact]
    public void A_cancelled_order_in_the_run_ticks_nothing_and_is_counted_once()
    {
        var cancelled = Order("SPA053 USD", new DateTime(2026, 10, 26, 14, 0, 0), status: SalesOrderStatus.Cancelled);
        var lastWeek = Order("SPA053 USD", new DateTime(2026, 10, 19, 14, 0, 0), status: SalesOrderStatus.Cancelled);

        var checklist = Runs(Tuesday27, cancelled, lastWeek);

        Assert.False(ShopOf(checklist, "BORROWDALE", "SPA053 USD").HasOrdered);
        Assert.Equal(1, checklist.SkippedOrderCount);
    }

    [Fact]
    public void Order_date_matching_still_ticks_every_order_it_is_given()
    {
        var order = Order("SPA053 USD", new DateTime(2026, 10, 26, 14, 0, 0));

        var checklist = RouteOrderChecklist.Build(DeliveryRouteDirectory.Build([]), [order]);

        Assert.Null(RouteOf(checklist, "BORROWDALE").RunDate);
        Assert.True(ShopOf(checklist, "BORROWDALE", "SPA053 USD").HasOrdered);
    }

    [Theory]
    [InlineData(new[] { "Tuesday" }, new[] { DayOfWeek.Tuesday })]
    [InlineData(new[] { "Monday", "Friday" }, new[] { DayOfWeek.Monday, DayOfWeek.Friday })]
    [InlineData(new[] { " wed " }, new[] { DayOfWeek.Wednesday })]
    [InlineData(new string[0], new DayOfWeek[0])]
    public void Run_days_are_read_from_the_routes_day_names(string[] names, DayOfWeek[] expected)
    {
        var route = new RouteSummary("TEST", "Test", names, null, true, 0);

        Assert.Equal(expected.Order(), RouteOrderChecklist.RunDays(route).Order());
    }

    [Theory]
    [InlineData("2026-10-26T21:59:00Z", "2026-10-26")]
    [InlineData("2026-10-26T22:00:00Z", "2026-10-27")]
    public void The_Harare_day_turns_at_22_00_UTC(string instant, string expected) =>
        Assert.Equal(
            DateTime.Parse(expected),
            RouteOrderChecklist.HarareDay(DateTime.Parse(instant, null, System.Globalization.DateTimeStyles.AdjustToUniversal)));
}
