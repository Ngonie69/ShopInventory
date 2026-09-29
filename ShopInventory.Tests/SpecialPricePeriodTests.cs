using System.Text.Json;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// The validity window a special price is stored with when a period row priced it.
/// </summary>
/// <remarks>
/// The catalogue filters on the stored window between syncs, which are manual now. A period price
/// stored under the header's open-ended window would still be charged after the period ended.
/// Which row wins is covered in <see cref="SpecialPriceDataAreaTests"/>.
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

    [Fact]
    public void A_current_period_is_stored_with_its_own_window()
    {
        var price = SAPServiceLayerClient.ParseCurrentBusinessPartnerSpecialPrice(Record(CurrentPeriod), Today);

        Assert.NotNull(price);
        Assert.Equal(1.298701m, price.Price);
        Assert.Equal(new DateTime(2026, 1, 1), price.ValidFrom);
        Assert.Equal(new DateTime(2026, 12, 31), price.ValidTo);
    }

    [Fact]
    public void A_lapsed_period_is_passed_over_for_the_current_one_and_its_window()
    {
        var price = SAPServiceLayerClient.ParseCurrentBusinessPartnerSpecialPrice(
            Record(LapsedPeriod + "," + CurrentPeriod), Today);

        Assert.NotNull(price);
        Assert.Equal(1.298701m, price.Price);
        Assert.Equal(new DateTime(2026, 12, 31), price.ValidTo);
    }
}
