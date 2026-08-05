using System.Text.Json;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Covers the parsing and posting halves of the zero-priced line that reached SAP on order 80151.
/// </summary>
/// <remarks>
/// YOG101's real payload shows an empty UoMPrices on every one of its ~100 ItemPrices rows and a
/// 0.55 USD price on lists 11 and 96, so the per-UoM shape covered below is defensive rather than
/// the cause of that order. What the payload does show is a 0.00 row on list 1 and on ~30 other
/// unused lists, which is why the price list a customer resolves to has to be the real one and
/// never a default.
/// </remarks>
public class SalesOrderLinePricingTests
{
    private const int PriceList = 2;

    private static JsonElement ItemPrices(string json)
        => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void A_priced_header_row_is_used_as_is()
    {
        // Trimmed from YOG101's real payload: list 1 is 0.00 with a null currency, list 11 is 0.55.
        var prices = ItemPrices("""
            [
              { "PriceList": 1, "Price": 0.0, "Currency": null, "BasePriceList": 1, "Factor": 1.0, "UoMPrices": [] },
              { "PriceList": 2, "Price": 0.55, "Currency": "USD", "BasePriceList": 11, "Factor": 1.0, "UoMPrices": [] }
            ]
            """);

        var resolved = SAPServiceLayerClient.ResolveItemPriceForList(prices, PriceList, out _);

        Assert.NotNull(resolved);
        Assert.Equal(0.55m, resolved!.Value.Price);
        Assert.Equal("USD", resolved.Value.Currency);
    }

    [Fact]
    public void An_empty_uom_price_list_leaves_a_zero_row_unpriced()
    {
        // The actual YOG101 shape on its unused lists: a 0.00 header and an empty UoMPrices. There
        // is genuinely no price here, so the item must stay unpriced and the posting guard must be
        // what stops it — this is not a case the parser can rescue.
        var prices = ItemPrices("""
            [
              { "PriceList": 2, "Price": 0.0, "Currency": "", "BasePriceList": 2, "Factor": 1.0, "UoMPrices": [] }
            ]
            """);

        Assert.Null(SAPServiceLayerClient.ResolveItemPriceForList(prices, PriceList, out _));
    }

    [Fact]
    public void A_zero_header_falls_through_to_the_per_uom_price()
    {
        // Defensive: B1 does hold per-UoM prices this way, though YOG101 does not use them.
        var prices = ItemPrices("""
            [
              {
                "PriceList": 2,
                "Price": 0,
                "Currency": "USD",
                "UoMPrices": [
                  { "PriceList": 2, "UoMEntry": 4, "Price": 0.55, "Currency": "USD" }
                ]
              }
            ]
            """);

        var resolved = SAPServiceLayerClient.ResolveItemPriceForList(prices, PriceList, out _);

        Assert.NotNull(resolved);
        Assert.Equal(0.55m, resolved!.Value.Price);
        Assert.Equal("USD", resolved.Value.Currency);
    }

    [Fact]
    public void A_null_header_price_falls_through_to_the_per_uom_price()
    {
        var prices = ItemPrices("""
            [
              {
                "PriceList": 2,
                "Price": null,
                "UoMPrices": [
                  { "UoMEntry": 4, "Price": 0.37, "Currency": "USD" }
                ]
              }
            ]
            """);

        var resolved = SAPServiceLayerClient.ResolveItemPriceForList(prices, PriceList, out _);

        Assert.NotNull(resolved);
        Assert.Equal(0.37m, resolved!.Value.Price);
        Assert.Equal("USD", resolved.Value.Currency);
    }

    [Fact]
    public void A_zero_row_no_longer_abandons_a_later_priced_row_for_the_same_list()
    {
        // Duplicate rows per list occur — DeduplicateItemPriceRows exists because of them. The old
        // parser broke out of the loop on the first zero and never saw the row that was priced.
        var prices = ItemPrices("""
            [
              { "PriceList": 2, "Price": 0, "Currency": "USD" },
              { "PriceList": 2, "Price": 0.40, "Currency": "USD" }
            ]
            """);

        var resolved = SAPServiceLayerClient.ResolveItemPriceForList(prices, PriceList, out _);

        Assert.NotNull(resolved);
        Assert.Equal(0.40m, resolved!.Value.Price);
    }

    [Fact]
    public void A_per_uom_row_without_a_currency_inherits_the_entry_it_sits_in()
    {
        var prices = ItemPrices("""
            [
              {
                "PriceList": 2,
                "Price": 0,
                "Currency": "ZIG",
                "UoMPrices": [ { "UoMEntry": 4, "Price": 12.50 } ]
              }
            ]
            """);

        var resolved = SAPServiceLayerClient.ResolveItemPriceForList(prices, PriceList, out _);

        Assert.NotNull(resolved);
        Assert.Equal("ZIG", resolved!.Value.Currency);
    }

    [Fact]
    public void Per_uom_prices_on_another_price_list_are_ignored()
    {
        var prices = ItemPrices("""
            [
              {
                "PriceList": 2,
                "Price": 0,
                "UoMPrices": [
                  { "PriceList": 7, "UoMEntry": 4, "Price": 99.00, "Currency": "USD" }
                ]
              }
            ]
            """);

        Assert.Null(SAPServiceLayerClient.ResolveItemPriceForList(prices, PriceList, out _));
    }

    [Fact]
    public void Disagreeing_per_uom_prices_leave_the_item_unpriced_rather_than_guessing()
    {
        // The lookup is not told which UoM the order line uses, so picking one would risk posting
        // a wrong price. Unpriced is recoverable — the posting guard names the item.
        var prices = ItemPrices("""
            [
              {
                "PriceList": 2,
                "Price": 0,
                "UoMPrices": [
                  { "UoMEntry": 4, "Price": 0.55, "Currency": "USD" },
                  { "UoMEntry": 9, "Price": 6.60, "Currency": "USD" }
                ]
              }
            ]
            """);

        Assert.Null(SAPServiceLayerClient.ResolveItemPriceForList(prices, PriceList, out var ambiguous));
        Assert.Equal([0.55m, 6.60m], ambiguous);
    }

    [Fact]
    public void Repeated_per_uom_rows_agreeing_on_one_price_are_not_ambiguous()
    {
        var prices = ItemPrices("""
            [
              {
                "PriceList": 2,
                "Price": 0,
                "UoMPrices": [
                  { "UoMEntry": 4, "Price": 0.55, "Currency": "USD" },
                  { "UoMEntry": 9, "Price": 0.55, "Currency": "USD" }
                ]
              }
            ]
            """);

        var resolved = SAPServiceLayerClient.ResolveItemPriceForList(prices, PriceList, out _);

        Assert.NotNull(resolved);
        Assert.Equal(0.55m, resolved!.Value.Price);
    }

    [Fact]
    public void An_item_absent_from_the_price_list_stays_unpriced()
    {
        var prices = ItemPrices("""[ { "PriceList": 1, "Price": 9.99, "Currency": "USD" } ]""");

        Assert.Null(SAPServiceLayerClient.ResolveItemPriceForList(prices, PriceList, out _));
    }

    [Fact]
    public void A_fully_priced_order_reports_nothing_unpriced()
    {
        var order = OrderWith(("YOG127", 0.40m), ("YOG101", 0.55m));

        Assert.Empty(SalesOrderService.FindUnpricedItemCodes(order));
    }

    [Fact]
    public void A_zero_priced_line_is_reported_by_item_code()
    {
        // Order 80151: seven priced lines and YOG101 at 0.00, which SAP accepted verbatim.
        var order = OrderWith(("YOG127", 0.40m), ("YOG101", 0m), ("DAI008", 0.37m));

        Assert.Equal(["YOG101"], SalesOrderService.FindUnpricedItemCodes(order));
    }

    [Fact]
    public void A_negative_price_is_reported_too()
    {
        var order = OrderWith(("YOG127", -1m));

        Assert.Equal(["YOG127"], SalesOrderService.FindUnpricedItemCodes(order));
    }

    [Fact]
    public void Each_unpriced_item_is_named_once()
    {
        var order = OrderWith(("YOG101", 0m), ("YOG101", 0m), ("DAI008", 0m));

        Assert.Equal(["YOG101", "DAI008"], SalesOrderService.FindUnpricedItemCodes(order));
    }

    private static SalesOrderEntity OrderWith(params (string ItemCode, decimal UnitPrice)[] lines)
    {
        var order = new SalesOrderEntity
        {
            OrderNumber = "SO-TEST-80151",
            CardCode = "NRI049"
        };

        var lineNum = 0;
        foreach (var (itemCode, unitPrice) in lines)
        {
            order.Lines.Add(new SalesOrderLineEntity
            {
                LineNum = lineNum++,
                ItemCode = itemCode,
                Quantity = 60,
                UnitPrice = unitPrice
            });
        }

        return order;
    }
}
