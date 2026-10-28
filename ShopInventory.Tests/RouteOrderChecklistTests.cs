using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The route checklist on Retail Sales Orders: which shops on each delivery route have ordered.
/// Built over the generated routes catalogue, so the shops named here are real stops.
/// </summary>
public sealed class RouteOrderChecklistTests
{
    private static int _nextId = 1;

    private static SalesOrderDto Order(string cardCode, SalesOrderStatus status = SalesOrderStatus.Pending) => new()
    {
        Id = _nextId++,
        OrderNumber = $"SO-{_nextId}",
        CardCode = cardCode,
        CardName = cardCode,
        Status = status,
        OrderDate = new DateTime(2026, 10, 28, 9, 0, 0)
    };

    private static RouteChecklistRoute RouteOf(RouteChecklist checklist, string name) =>
        checklist.Routes.Single(route => route.Route.Name == name);

    private static RouteChecklistShop SparBridge(RouteChecklist checklist) =>
        RouteOf(checklist, "BORROWDALE").Shops.Single(shop => shop.CardCodes.Contains("SPA050 USD"));

    [Fact]
    public void A_shops_currency_codes_are_one_row_ticked_by_an_order_on_any_of_them()
    {
        var map = DeliveryRouteDirectory.Build([]);

        var checklist = RouteOrderChecklist.Build(map, [Order("SPA070")]);

        var shop = SparBridge(checklist);
        Assert.Equal(["SPA002", "SPA050 USD", "SPA070"], shop.CardCodes);
        Assert.Equal("Spar Bridge Yellowcob Ent P/L t/a", shop.Name);
        Assert.True(shop.HasOrdered);
        Assert.Single(shop.Orders);
        Assert.Empty(checklist.OffRouteOrders);
    }

    [Fact]
    public void A_shop_with_no_order_is_left_unticked()
    {
        var checklist = RouteOrderChecklist.Build(DeliveryRouteDirectory.Build([]), [Order("SPA070")]);

        var borrowdale = RouteOf(checklist, "BORROWDALE");
        Assert.Contains(borrowdale.Shops, shop => !shop.HasOrdered);
        Assert.Equal(1, borrowdale.OrderedCount);
    }

    [Fact]
    public void Retired_currency_codes_are_left_out_unless_they_carry_an_order()
    {
        var map = DeliveryRouteDirectory.Build([]);
        var standings = new Dictionary<string, ShopStanding>(StringComparer.OrdinalIgnoreCase)
        {
            ["SPA002"] = new(IsFrozen: false, IsRetiredCurrency: true)
        };

        var quiet = RouteOrderChecklist.Build(map, [], standings);
        Assert.Equal(["SPA050 USD", "SPA070"], SparBridge(quiet).CardCodes);

        var ordered = RouteOrderChecklist.Build(map, [Order("SPA002")], standings);
        Assert.Equal(["SPA002", "SPA050 USD", "SPA070"], SparBridge(ordered).CardCodes);
        Assert.True(SparBridge(ordered).HasOrdered);
    }

    [Fact]
    public void A_shop_is_on_hold_only_when_every_code_is_frozen()
    {
        var map = DeliveryRouteDirectory.Build([]);
        var oneFrozen = new Dictionary<string, ShopStanding>(StringComparer.OrdinalIgnoreCase)
        {
            ["SPA050 USD"] = new(IsFrozen: true, IsRetiredCurrency: false)
        };
        Assert.False(SparBridge(RouteOrderChecklist.Build(map, [], oneFrozen)).IsOnHold);

        var allFrozen = new Dictionary<string, ShopStanding>(StringComparer.OrdinalIgnoreCase)
        {
            ["SPA002"] = new(IsFrozen: true, IsRetiredCurrency: false),
            ["SPA050 USD"] = new(IsFrozen: true, IsRetiredCurrency: false),
            ["SPA070"] = new(IsFrozen: true, IsRetiredCurrency: false)
        };
        Assert.True(SparBridge(RouteOrderChecklist.Build(map, [], allFrozen)).IsOnHold);
    }

    [Fact]
    public void An_order_from_a_shop_on_no_route_is_listed_apart()
    {
        // TM Lobengula is a Bulawayo shop the workbook leaves off every route.
        var order = Order("TMP114");

        var checklist = RouteOrderChecklist.Build(DeliveryRouteDirectory.Build([]), [order]);

        Assert.Equal([order], checklist.OffRouteOrders);
        Assert.All(checklist.Routes, route => Assert.Equal(0, route.OrderCount));
    }

    [Fact]
    public void A_shop_moved_onto_a_route_on_the_routes_page_is_checked_there()
    {
        var map = DeliveryRouteDirectory.Build(
            [new() { CardCode = "TMP114", CardName = "TM Lobengula", RouteName = "MIDLANDS 1", IsRemoval = false }]);

        var checklist = RouteOrderChecklist.Build(map, [Order("TMP114")]);

        Assert.Empty(checklist.OffRouteOrders);
        Assert.Contains(RouteOf(checklist, "MIDLANDS 1").Shops, shop => shop.HasOrdered && shop.CardCodes.Contains("TMP114"));
    }

    [Theory]
    [InlineData(SalesOrderStatus.Cancelled)]
    [InlineData(SalesOrderStatus.Rejected)]
    public void A_cancelled_or_rejected_order_ticks_nothing(SalesOrderStatus status)
    {
        var checklist = RouteOrderChecklist.Build(DeliveryRouteDirectory.Build([]), [Order("SPA070", status)]);

        Assert.False(SparBridge(checklist).HasOrdered);
        Assert.Equal(1, checklist.SkippedOrderCount);
        Assert.Empty(checklist.OffRouteOrders);
    }

    [Fact]
    public void Card_codes_match_whatever_their_case_and_spacing()
    {
        var checklist = RouteOrderChecklist.Build(DeliveryRouteDirectory.Build([]), [Order(" spa050 usd ")]);

        Assert.True(SparBridge(checklist).HasOrdered);
    }

    [Theory]
    [InlineData("Spar Bridge Yellowcob Ent P/L t/a ZiG", "spar bridge yellowcob ent p l t a")]
    [InlineData("Cheese Galore RTGS", "cheese galore")]
    [InlineData("Cheese Galore (FCA)", "cheese galore")]
    [InlineData("Cheese Galore (Packaging)", "cheese galore packaging")]
    [InlineData("  ", "")]
    public void The_shop_key_drops_the_currency_words(string name, string expected) =>
        Assert.Equal(expected, RouteOrderChecklist.ShopKey(name));
}
