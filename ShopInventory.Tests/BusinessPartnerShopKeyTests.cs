using ShopInventory.Web.Common;
using Xunit;

namespace ShopInventory.Tests;

/// <summary>
/// The shop a partner name names, which the customer drawer uses to offer a WhatsApp number to the
/// same shop's cards in its other currencies.
/// </summary>
/// <remarks>
/// The expected keys were produced by running <c>name_key</c> in
/// <c>scripts/DeliveryRoutes/generate_delivery_routes.py</c> on the same names, so these pin the port
/// to the original rather than to what the port happens to do. That includes the original's two
/// quirks: "BOND" is a currency word wherever it stands alone, and "US$" in mid-name survives,
/// because a word boundary cannot follow the dollar sign there.
/// </remarks>
public sealed class BusinessPartnerShopKeyTests
{
    [Theory]
    [InlineData("Spar Bridge", "SPAR BRIDGE")]
    [InlineData("SPAR BRIDGE USD", "SPAR BRIDGE")]
    [InlineData("Spar Bridge (ZiG)", "SPAR BRIDGE")]
    [InlineData("Spar Bridge - FCA", "SPAR BRIDGE")]
    [InlineData("spar bridge rtgs", "SPAR BRIDGE")]
    [InlineData("Spar Bridge.", "SPAR BRIDGE")]
    [InlineData("Abbiamo Trading Deli Spices", "ABBIAMO TRADING DELI SPICES")]
    [InlineData("ABBIAMO TRADING DELI SPICES (FCA)", "ABBIAMO TRADING DELI SPICES")]
    [InlineData("Abbiamo Trading Deli Spices  USD", "ABBIAMO TRADING DELI SPICES")]
    [InlineData("Bon Marché Borrowdale", "BON MARCHE BORROWDALE")]
    [InlineData("Bon Marche Borrowdale USD", "BON MARCHE BORROWDALE")]
    [InlineData("O'Hagan's Bar & Grill", "OHAGANS BAR AND GRILL")]
    [InlineData("TM Pick n Pay - Avondale ZIG", "TM PICK N PAY AVONDALE")]
    [InlineData("Food World/Westgate RTGS", "FOOD WORLD WESTGATE")]
    [InlineData("St. John's Butchery", "ST JOHNS BUTCHERY")]
    [InlineData("Bondi Supermarket", "BONDI SUPERMARKET")]
    [InlineData("Usdan Stores", "USDAN STORES")]
    [InlineData("Bond Street Spar", "STREET SPAR")]
    [InlineData("Spar US$ Bridge", "SPAR US$ BRIDGE")]
    public void Matches_the_delivery_routes_name_key(string name, string expected)
    {
        Assert.Equal(expected, BusinessPartnerShopKey.For(name));
    }

    [Fact]
    public void A_shops_currency_cards_share_one_key()
    {
        var keys = new[] { "Spar Bridge", "SPAR BRIDGE USD", "Spar Bridge (ZiG)", "Spar Bridge - FCA" }
            .Select(BusinessPartnerShopKey.For)
            .Distinct()
            .ToList();

        Assert.Single(keys);
    }

    [Fact]
    public void Different_shops_do_not_share_a_key()
    {
        Assert.NotEqual(BusinessPartnerShopKey.For("Spar Bridge USD"), BusinessPartnerShopKey.For("Spar Borrowdale USD"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("(USD)")]
    public void A_name_with_nothing_left_has_an_empty_key(string? name)
    {
        Assert.Equal(string.Empty, BusinessPartnerShopKey.For(name));
    }
}
