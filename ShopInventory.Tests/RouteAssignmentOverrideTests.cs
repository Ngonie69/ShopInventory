using ShopInventory.Web.Common;
using ShopInventory.Web.Data;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The /route-assignments page stores what people change as deltas against the
/// generated catalogue rather than as a copy of it, so that regenerating from a
/// newer routes workbook keeps their corrections. These cover the merge: what
/// the report sees once an override is applied.
/// </summary>
public sealed class RouteAssignmentOverrideTests
{
    private static RouteAssignmentOverride Add(string cardCode, string route) =>
        new() { CardCode = cardCode, RouteName = route, IsRemoval = false, CardName = cardCode };

    private static RouteAssignmentOverride Remove(string cardCode, string route) =>
        new() { CardCode = cardCode, RouteName = route, IsRemoval = true, CardName = cardCode };

    private static CustomDeliveryRoute Route(string name, string? days = null) =>
        new() { Name = name, Days = days };

    [Fact]
    public void With_no_overrides_the_map_is_the_generated_catalogue()
    {
        var map = DeliveryRouteDirectory.Build([]);

        Assert.Equal(DeliveryRoutes.Names, map.Names);
        foreach (var route in DeliveryRoutes.All)
        {
            Assert.Equal(route.CardCodes.Count, map.GetStops(route.Name).Count);
        }

        Assert.True(map.IsOnRoute("SPA059 USD", "WEST 2"));
        Assert.Empty(map.GetRoutes("TMP114"));
    }

    [Fact]
    public void An_added_shop_appears_on_the_route()
    {
        // TM Lobengula is a Bulawayo shop the workbook deliberately leaves off.
        var map = DeliveryRouteDirectory.Build([Add("TMP114", "MIDLANDS 1")]);

        Assert.True(map.IsOnRoute("TMP114", "MIDLANDS 1"));
        Assert.Contains(map.GetStops("MIDLANDS 1"), stop => stop.CardCode == "TMP114");
    }

    [Fact]
    public void A_removed_shop_drops_off_the_route()
    {
        Assert.True(DeliveryRoutes.IsOnRoute("SPA059 USD", "WEST 2"));

        var map = DeliveryRouteDirectory.Build([Remove("SPA059 USD", "WEST 2")]);

        Assert.False(map.IsOnRoute("SPA059 USD", "WEST 2"));
        Assert.DoesNotContain(map.GetStops("WEST 2"), stop => stop.CardCode == "SPA059 USD");
    }

    /// <summary>
    /// A move is a removal plus an assignment, which is how the page records it.
    /// </summary>
    [Fact]
    public void A_move_takes_the_shop_off_one_route_and_puts_it_on_the_other()
    {
        var map = DeliveryRouteDirectory.Build(
            [Remove("SPA059 USD", "WEST 2"), Add("SPA059 USD", "WEST 1")]);

        Assert.False(map.IsOnRoute("SPA059 USD", "WEST 2"));
        Assert.True(map.IsOnRoute("SPA059 USD", "WEST 1"));
        Assert.Equal("WEST 1", map.FormatRoutes("SPA059 USD"));
    }

    /// <summary>
    /// The page marks a shop somebody put on a route, so a reader can tell it
    /// apart from one the workbook places there.
    /// </summary>
    [Fact]
    public void A_moved_shop_is_marked_and_a_workbook_one_is_not()
    {
        var map = DeliveryRouteDirectory.Build([Add("TMP114", "MIDLANDS 1")]);

        Assert.True(map.IsReassigned("TMP114", "MIDLANDS 1"));
        Assert.False(map.IsReassigned("SPA059 USD", "WEST 2"));

        var added = map.GetStops("MIDLANDS 1").Single(stop => stop.CardCode == "TMP114");
        Assert.False(added.FromWorkbook);
        var original = map.GetStops("WEST 2").First(stop => stop.CardCode == "SPA059 USD");
        Assert.True(original.FromWorkbook);
    }

    /// <summary>
    /// The workbook gets regenerated and a route can disappear with it. A row
    /// naming a route that no longer exists is stale, and stale data must not
    /// take the report down.
    /// </summary>
    [Fact]
    public void An_override_for_a_route_that_no_longer_exists_is_ignored()
    {
        var map = DeliveryRouteDirectory.Build(
            [Add("TMP114", "A ROUTE THAT WAS DROPPED"), Add("TMP110", "MIDLANDS 1")]);

        Assert.Empty(map.GetRoutes("TMP114"));
        Assert.True(map.IsOnRoute("TMP110", "MIDLANDS 1"));
        Assert.DoesNotContain("A ROUTE THAT WAS DROPPED", map.Names);
    }

    /// <summary>
    /// Codes carry their currency as a suffix, and a person typing one into the
    /// page will not reproduce SAP's spacing. The merge normalises, so the
    /// override still finds the shop.
    /// </summary>
    [Theory]
    [InlineData("spa059 usd")]
    [InlineData("  SPA059   USD ")]
    public void An_override_matches_however_the_code_was_spaced(string typed)
    {
        var map = DeliveryRouteDirectory.Build([Remove(typed, "WEST 2")]);

        Assert.False(map.IsOnRoute("SPA059 USD", "WEST 2"));
    }

    /// <summary>
    /// An emptied route drops out of the filter -- offering a route that selects
    /// nothing is a dead option -- but stays in the management list, because it
    /// has to be reachable to put shops back on it.
    /// </summary>
    [Fact]
    public void A_route_emptied_by_removals_leaves_the_filter_but_not_the_page()
    {
        var honeydew = DeliveryRoutes.All.Single(route => route.Name == "HONEYDEW");
        var removals = honeydew.CardCodes.Select(code => Remove(code, "HONEYDEW")).ToList();

        var map = DeliveryRouteDirectory.Build(removals);

        Assert.DoesNotContain("HONEYDEW", map.Names);
        Assert.Empty(map.GetStops("HONEYDEW"));
        Assert.Contains(map.AllRoutes, route => route.Name == "HONEYDEW" && route.StopCount == 0);
    }

    /// <summary>
    /// A route somebody added shows up alongside the workbook's own, and is
    /// marked so a reader can tell which is which.
    /// </summary>
    [Fact]
    public void An_added_route_joins_the_list()
    {
        var map = DeliveryRouteDirectory.Build([], [Route("BULAWAYO LOCAL", "Thursday")]);

        var added = map.AllRoutes.Single(route => route.Name == "BULAWAYO LOCAL");
        Assert.True(added.IsCustom);
        Assert.Equal("BULAWAYO LOCAL (Thu)", added.Label);
        Assert.Equal(0, added.StopCount);

        // Workbook routes keep their own flag.
        Assert.False(map.AllRoutes.Single(route => route.Name == "WEST 2").IsCustom);
    }

    /// <summary>
    /// The whole point of adding a route is to put shops on it, so a route with
    /// none must stay visible -- otherwise it vanishes the moment it is created.
    /// </summary>
    [Fact]
    public void A_new_route_stays_visible_while_it_is_still_empty()
    {
        var map = DeliveryRouteDirectory.Build([], [Route("BULAWAYO LOCAL", "Thursday")]);

        Assert.Contains(map.AllRoutes, route => route.Name == "BULAWAYO LOCAL");
        // ...but it is not offered as a report filter until it has shops.
        Assert.DoesNotContain("BULAWAYO LOCAL", map.Names);
    }

    [Fact]
    public void A_shop_assigned_to_an_added_route_lands_on_it()
    {
        var map = DeliveryRouteDirectory.Build(
            [Add("TMP114", "BULAWAYO LOCAL")], [Route("BULAWAYO LOCAL", "Thursday")]);

        Assert.True(map.IsOnRoute("TMP114", "BULAWAYO LOCAL"));
        Assert.True(map.IsReassigned("TMP114", "BULAWAYO LOCAL"));
        Assert.Contains("BULAWAYO LOCAL", map.Names);
        Assert.Equal(1, map.AllRoutes.Single(route => route.Name == "BULAWAYO LOCAL").StopCount);
    }

    /// <summary>
    /// An added route must never shadow a workbook one, or the workbook's stops
    /// would silently vanish behind an empty route of the same name.
    /// </summary>
    [Fact]
    public void An_added_route_cannot_shadow_a_workbook_route()
    {
        var map = DeliveryRouteDirectory.Build([], [Route("WEST 2", "Friday")]);

        var west2 = map.AllRoutes.Single(route => route.Name == "WEST 2");
        Assert.False(west2.IsCustom);
        Assert.True(west2.StopCount > 0);
        Assert.True(map.IsOnRoute("SPA059 USD", "WEST 2"));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("Tuesday", "Tuesday")]
    [InlineData("Monday,Friday", "Monday|Friday")]
    [InlineData(" Monday , Friday ", "Monday|Friday")]
    public void Route_days_round_trip_through_the_stored_text(string? stored, string expected)
    {
        Assert.Equal(expected, string.Join('|', DeliveryRouteDirectory.SplitDays(stored)));
    }

    [Fact]
    public void The_map_carries_the_shop_name_for_a_code_it_holds()
    {
        Assert.Equal("SPAR Athienitis Yellowcob Ent P/: t/a", DeliveryRoutes.GetCardName("SPA059 USD"));
        Assert.Equal("SPAR Athienitis Yellowcob Ent P/: t/a", DeliveryRoutes.GetCardName("spa059  usd"));
        Assert.Null(DeliveryRoutes.GetCardName("NOT A CODE"));

        var map = DeliveryRouteDirectory.Build([]);
        var stop = map.GetStops("WEST 2").First(s => s.CardCode == "SPA059 USD");
        Assert.Equal("SPAR Athienitis Yellowcob Ent P/: t/a", stop.CardName);
    }
}
