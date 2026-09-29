using System.Globalization;
using System.Text.Json;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Covers reading the period rows (SAP's SPP1, "Period and Volume Discounts") of a business
/// partner special price.
/// </summary>
/// <remarks>
/// The rows were looked up as "SpecialPricesDataAreas", which SAP never sends, so they were never
/// read and the header price was used even while a period price was in force. The property is
/// "SpecialPriceDataAreas", and a row spells its end date "Dateto".
/// </remarks>
public sealed class SpecialPriceDataAreaTests
{
    private static readonly DateTime Today = new(2026, 9, 29);

    [Fact]
    public void Period_row_in_force_today_wins_over_the_header()
    {
        var item = SpecialPrice(headerPrice: 2.10m, areas: Area("2026-09-01", "2026-09-30", 1.75m));

        Assert.True(SAPServiceLayerClient.TryExtractCurrentSpecialPrice(item, Today, out var price));
        Assert.Equal(1.75m, price);
    }

    [Fact]
    public void Expired_period_row_falls_back_to_the_header_price()
    {
        // SAP does the same: GetItemPrice priced MAC002/YOG001 at its header special price after
        // the only period row had ended, while the header was still valid.
        var item = SpecialPrice(headerPrice: 2.10m, areas: Area("2026-08-01", "2026-08-31", 1.75m));

        Assert.True(SAPServiceLayerClient.TryExtractCurrentSpecialPrice(item, Today, out var price));
        Assert.Equal(2.10m, price);
    }

    [Fact]
    public void Empty_period_rows_use_the_header_price()
    {
        // The shape of the live FET010 / CIS006 response on 2026-09-29.
        var item = SpecialPrice(headerPrice: 2.10m);

        Assert.True(SAPServiceLayerClient.TryExtractCurrentSpecialPrice(item, Today, out var price));
        Assert.Equal(2.10m, price);
    }

    [Fact]
    public void Expired_period_row_and_expired_header_leave_no_special_price()
    {
        // Then SAP prices from the price list.
        var item = SpecialPrice(
            headerPrice: 2.10m,
            headerValidTo: "2026-09-28",
            areas: Area("2026-08-01", "2026-08-31", 1.75m));

        Assert.False(SAPServiceLayerClient.TryExtractCurrentSpecialPrice(item, Today, out _));
    }

    [Fact]
    public void Catalogue_sync_stores_the_period_price_in_force()
    {
        var item = SpecialPrice(headerPrice: 2.10m, areas: Area("2026-09-01", "2026-09-30", 1.75m));

        var special = SAPServiceLayerClient.ParseCurrentBusinessPartnerSpecialPrice(item, Today);

        Assert.NotNull(special);
        Assert.Equal("CIS006", special.CardCode);
        Assert.Equal("FET010", special.ItemCode);
        Assert.Equal(1.75m, special.Price);
    }

    private static string Area(string dateFrom, string dateTo, decimal specialPrice) => $$"""
        {
          "PriceCurrency": "USD",
          "AutoUpdate": "tNO",
          "Dateto": "{{dateTo}}T00:00:00Z",
          "Discount": 20.0,
          "SpecialPrice": {{specialPrice.ToString(CultureInfo.InvariantCulture)}},
          "DateFrom": "{{dateFrom}}T00:00:00Z",
          "BPCode": "CIS006",
          "PriceListNo": 1,
          "ItemNo": "FET010",
          "RowNumber": 0,
          "SpecialPriceQuantityAreas": []
        }
        """;

    private static JsonElement SpecialPrice(decimal headerPrice, string? headerValidTo = null, params string[] areas)
    {
        var validTo = headerValidTo is null ? "null" : $"\"{headerValidTo}T00:00:00Z\"";

        return JsonDocument.Parse($$"""
            {
              "ItemCode": "FET010",
              "CardCode": "CIS006",
              "Price": {{headerPrice.ToString(CultureInfo.InvariantCulture)}},
              "Currency": "USD",
              "DiscountPercent": 4.545455,
              "PriceListNum": 1,
              "AutoUpdate": "tNO",
              "SourcePrice": "spPrimaryCurrency",
              "Valid": "tYES",
              "ValidFrom": null,
              "ValidTo": {{validTo}},
              "SpecialPriceDataAreas": [{{string.Join(",", areas)}}]
            }
            """).RootElement;
    }
}
