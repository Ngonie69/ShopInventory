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

    // ------------------------------------------------------------------
    // The currency a code is for, which is what tells two rows for the same
    // shop apart on the page.
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("SPA059 USD", "USD")]
    [InlineData("spa059  usd", "USD")]
    [InlineData("CHE016 ZIG", "ZiG")]
    [InlineData("CHE005 (FCA)", "FCA")]
    [InlineData("CHE005 FCA", "FCA")]
    [InlineData("SPA002", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void A_codes_currency_comes_off_its_suffix(string? cardCode, string? expected)
    {
        Assert.Equal(expected, DeliveryRoutes.CurrencyOf(cardCode));
    }

    [Fact]
    public void A_shop_name_that_happens_to_end_in_a_word_is_not_read_as_a_currency()
    {
        // Only the four suffixes the catalogue uses count. Anything else is
        // left unbadged rather than guessed at.
        Assert.Null(DeliveryRoutes.CurrencyOf("ABC001 WHOLESALE"));
        Assert.Null(DeliveryRoutes.CurrencyOf("ABC001 ZAR"));
    }

    // ------------------------------------------------------------------
    // Retired currency. ZiG replaced ZWL in April 2024 and nothing has been
    // invoiced on a Zimbabwe dollar code since, so those codes are hidden from
    // the routes. Production writes "ZWL" and the test company writes "ZW$",
    // and matching only one of them would quietly do nothing in the other.
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("ZWL")]
    [InlineData("zwl")]
    [InlineData("ZW$")]
    [InlineData("ZWD")]
    [InlineData("RTGS")]
    [InlineData(" ZWL ")]
    public void A_zimbabwe_dollar_account_is_retired(string currency)
    {
        Assert.True(DeliveryRouteDirectory.IsRetiredCurrency(currency));
    }

    [Theory]
    [InlineData("USD")]
    [InlineData("ZiG")]
    [InlineData("ZAR")]
    [InlineData("EUR")]
    [InlineData("##")]   // SAP's any-currency marker
    [InlineData("")]
    [InlineData(null)]
    public void A_currency_still_in_use_is_not_retired(string? currency)
    {
        Assert.False(DeliveryRouteDirectory.IsRetiredCurrency(currency));
    }

    [Fact]
    public void ZiG_is_not_mistaken_for_the_dollar_it_replaced()
    {
        // "ZiG" contains no ZWL spelling, but the two are one character apart in
        // conversation and confusing them would hide every live local-currency
        // code on every route.
        Assert.False(DeliveryRouteDirectory.IsRetiredCurrency("ZiG"));
        Assert.True(DeliveryRouteDirectory.IsRetiredCurrency("ZWL"));
    }

    // ------------------------------------------------------------------
    // Whether an override says anything at all -- the rule the page asks
    // before staging a change and the write path asks before storing one.
    // ------------------------------------------------------------------

    [Fact]
    public void An_override_that_agrees_with_the_workbook_says_nothing()
    {
        // SPA059 USD is on WEST 2 in the workbook.
        Assert.False(DeliveryRouteDirectory.IsMeaningfulOverride("SPA059 USD", "WEST 2", isRemoval: false));
        Assert.True(DeliveryRouteDirectory.IsMeaningfulOverride("SPA059 USD", "WEST 2", isRemoval: true));

        // TMP114 is on no route in the workbook.
        Assert.True(DeliveryRouteDirectory.IsMeaningfulOverride("TMP114", "MIDLANDS 1", isRemoval: false));
        Assert.False(DeliveryRouteDirectory.IsMeaningfulOverride("TMP114", "MIDLANDS 1", isRemoval: true));
    }

    [Fact]
    public void A_meaningless_override_needs_a_code_and_a_route()
    {
        Assert.False(DeliveryRouteDirectory.IsMeaningfulOverride(null, "WEST 2", isRemoval: true));
        Assert.False(DeliveryRouteDirectory.IsMeaningfulOverride("SPA059 USD", "", isRemoval: true));
    }

    // ------------------------------------------------------------------
    // Projection: what the page draws while changes are still unsaved has to
    // match what the database would hold once they are.
    // ------------------------------------------------------------------

    private static RouteState State(params RouteAssignmentOverride[] saved) =>
        new(saved, []);

    [Fact]
    public void An_unsaved_change_shows_in_the_projection()
    {
        var map = DeliveryRouteDirectory.Project(
            State(),
            [new RouteChange("TMP114", "TM Somewhere", "MIDLANDS 1", IsRemoval: false)]);

        Assert.True(map.IsOnRoute("TMP114", "MIDLANDS 1"));
        Assert.Contains(map.GetStops("MIDLANDS 1"), stop => stop.CardCode == "TMP114");
    }

    [Fact]
    public void An_unsaved_change_replaces_the_saved_one_for_the_same_shop_and_route()
    {
        // Saved: TMP114 put on MIDLANDS 1. Unsaved: taken off again.
        var map = DeliveryRouteDirectory.Project(
            State(Add("TMP114", "MIDLANDS 1")),
            [new RouteChange("TMP114", null, "MIDLANDS 1", IsRemoval: true)]);

        Assert.False(map.IsOnRoute("TMP114", "MIDLANDS 1"));
    }

    [Fact]
    public void Reverting_a_saved_removal_puts_the_shop_back_rather_than_writing_a_second_row()
    {
        // Saved: SPA059 USD taken off WEST 2, where the workbook puts it.
        // Unsaved: put back -- which the workbook already agrees with, so the
        // right state is no override at all.
        var map = DeliveryRouteDirectory.Project(
            State(Remove("SPA059 USD", "WEST 2")),
            [new RouteChange("SPA059 USD", null, "WEST 2", IsRemoval: false)]);

        Assert.True(map.IsOnRoute("SPA059 USD", "WEST 2"));

        var stop = map.GetStops("WEST 2").Single(s => s.CardCode == "SPA059 USD");
        Assert.True(stop.FromWorkbook);
    }

    [Fact]
    public void A_move_staged_as_two_changes_lands_on_both_routes()
    {
        var map = DeliveryRouteDirectory.Project(
            State(),
            [
                new RouteChange("SPA059 USD", null, "WEST 2", IsRemoval: true),
                new RouteChange("SPA059 USD", null, "WEST 1", IsRemoval: false)
            ]);

        Assert.False(map.IsOnRoute("SPA059 USD", "WEST 2"));
        Assert.True(map.IsOnRoute("SPA059 USD", "WEST 1"));
        Assert.True(map.IsReassigned("SPA059 USD", "WEST 1"));
    }

    [Fact]
    public void Projecting_nothing_is_the_saved_map()
    {
        var saved = DeliveryRouteDirectory.Build([Add("TMP114", "MIDLANDS 1")]);
        var projected = DeliveryRouteDirectory.Project(State(Add("TMP114", "MIDLANDS 1")), []);

        Assert.Equal(saved.GetStops("MIDLANDS 1").Count, projected.GetStops("MIDLANDS 1").Count);
        Assert.True(projected.IsOnRoute("TMP114", "MIDLANDS 1"));
    }

    [Theory]
    [InlineData("SPA059 USD", "WEST 2")]
    [InlineData("spa059  usd", "west 2")]
    [InlineData(" SPA059   USD ", "WEST 2")]
    public void One_shop_and_route_is_one_key_however_it_was_typed(string code, string route)
    {
        Assert.Equal(
            DeliveryRouteDirectory.KeyOf("SPA059 USD", "WEST 2"),
            DeliveryRouteDirectory.KeyOf(code, route));
    }
}
