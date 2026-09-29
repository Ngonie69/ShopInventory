using System.Text.Json;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Which price a SAP special price record charges today when it carries period discounts.
/// </summary>
/// <remarks>
/// The periods were looked up as <c>SpecialPricesDataAreas</c>; the Service Layer sends
/// <c>SpecialPriceDataAreas</c> (checked against a live record on 2026-09-29). So every special
/// price was the header's, whatever period SAP was charging. Once the periods are read, their end
/// date matters too, and SAP spells it <c>Dateto</c>.
/// </remarks>
public sealed class SpecialPricePeriodTests
{
    private static readonly DateTime Today = new(2026, 9, 29);

    // Shaped like the Service Layer's SpecialPrices entity; the figures are illustrative.
    private static JsonElement Record(string periods) => JsonDocument.Parse($$"""
        {
          "ItemCode": "FET010",
          "CardCode": "CIS006",
          "Price": 0.276948,
          "Currency": "USD",
          "DiscountPercent": 78.67486,
          "PriceListNum": 107,
          "Valid": "tYES",
          "ValidFrom": null,
          "ValidTo": null,
          "SpecialPriceDataAreas": [{{periods}}]
        }
        """).RootElement;

    private const string CurrentPeriod = """
        { "RowNumber": 1, "DateFrom": "2026-01-01", "Dateto": "2026-12-31", "Discount": 78.67486,
          "SpecialPrice": 1.298701, "PriceListNo": 2, "PriceCurrency": "USD" }
        """;

    private const string LapsedPeriod = """
        { "RowNumber": 0, "DateFrom": "2026-06-01", "Dateto": "2026-06-30", "Discount": 50,
          "SpecialPrice": 0.649351, "PriceListNo": 107, "PriceCurrency": "USD" }
        """;

    private const string FuturePeriod = """
        { "RowNumber": 2, "DateFrom": "2026-10-01", "Dateto": "2026-10-31", "Discount": 50,
          "SpecialPrice": 0.649351, "PriceListNo": 107, "PriceCurrency": "USD" }
        """;

    [Fact]
    public void A_current_period_is_charged_over_the_header()
    {
        var price = SAPServiceLayerClient.ParseCurrentBusinessPartnerSpecialPrice(Record(CurrentPeriod), Today);

        Assert.NotNull(price);
        Assert.Equal(1.298701m, price.Price);
    }

    [Fact]
    public void A_current_period_is_stored_with_its_own_window()
    {
        // The catalogue filters on the stored window between syncs, which are manual now. Under the
        // header's open window the period would still be charged in January.
        var price = SAPServiceLayerClient.ParseCurrentBusinessPartnerSpecialPrice(Record(CurrentPeriod), Today);

        Assert.NotNull(price);
        Assert.Equal(new DateTime(2026, 1, 1), price.ValidFrom);
        Assert.Equal(new DateTime(2026, 12, 31), price.ValidTo);
    }

    [Fact]
    public void A_lapsed_period_is_passed_over_for_the_current_one()
    {
        var price = SAPServiceLayerClient.ParseCurrentBusinessPartnerSpecialPrice(
            Record(LapsedPeriod + "," + CurrentPeriod), Today);

        Assert.NotNull(price);
        Assert.Equal(1.298701m, price.Price);
    }

    [Fact]
    public void With_no_period_current_the_header_is_charged()
    {
        var price = SAPServiceLayerClient.ParseCurrentBusinessPartnerSpecialPrice(
            Record(LapsedPeriod + "," + FuturePeriod), Today);

        Assert.NotNull(price);
        Assert.Equal(0.276948m, price.Price);
        Assert.Null(price.ValidTo);
    }
}
