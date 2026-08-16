using ShopInventory.Configuration;

namespace ShopInventory.Tests;

/// <summary>
/// Pins the VAT a basket is charged and a receipt declares.
///
/// Two numbers come out of this and they must agree: what the customer pays, and what ZIMRA is told
/// they paid. VAT used to be worked out as one flat rate over the whole basket, so a zero-rated item
/// was charged VAT it does not attract — the customer overpaid, and the fiscal receipt asserted a tax
/// the sale did not carry.
///
/// The rates are keyed on the SAP tax code because that is the same key the fiscalisation platform's
/// tax ids are keyed on. If the two ever disagreed, a line would be charged at one rate and declared
/// at another.
/// </summary>
public sealed class SaleTaxTests
{
    private static TaxSettings Settings() => new()
    {
        VatRate = 0.155m,
        RatesByTaxCode = new(StringComparer.OrdinalIgnoreCase)
        {
            ["O01"] = 0.155m,
            ["O8"] = 0.155m,
            ["O0"] = 0m,
        }
    };

    [Theory]
    [InlineData("O01", 0.155)]
    [InlineData("O8", 0.155)]
    [InlineData("O0", 0.0)]
    public void A_line_is_rated_by_its_own_tax_code(string taxCode, double expected)
    {
        Assert.Equal((decimal)expected, Settings().RateFor(taxCode));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("SOMETHING-ELSE")]
    public void An_unknown_or_absent_tax_code_is_standard_rated(string? taxCode)
    {
        // Matches the fiscalisation settings' own DefaultTaxId, which is the standard-rated id — the
        // rate charged and the tax declared have to fall back the same way. It also keeps today's
        // behaviour for callers that send no tax codes at all.
        Assert.Equal(0.155m, Settings().RateFor(taxCode));
    }

    [Fact]
    public void Casing_and_padding_do_not_change_the_rate()
    {
        var tax = Settings();

        Assert.Equal(0m, tax.RateFor("o0"));
        Assert.Equal(0m, tax.RateFor("  O0  "));
    }

    [Fact]
    public void A_zero_rated_line_attracts_no_vat()
    {
        Assert.Equal(0m, Settings().VatOn(100m, "O0"));
    }

    [Fact]
    public void A_standard_rated_line_attracts_vat_at_the_standard_rate()
    {
        Assert.Equal(15.50m, Settings().VatOn(100m, "O01"));
    }

    [Fact]
    public void A_mixed_basket_is_taxed_line_by_line_not_in_aggregate()
    {
        // THE case. Two lines, one zero-rated. Flat-rating the basket charges 15.5% on all of it.
        var tax = Settings();

        var lines = new[]
        {
            (Net: 100m, TaxCode: "O01"),
            (Net: 100m, TaxCode: "O0"),
        };

        var perLine = lines.Sum(l => tax.VatOn(l.Net, l.TaxCode));
        var flatOverBasket = Math.Round(lines.Sum(l => l.Net) * tax.VatRate, 2);

        Assert.Equal(15.50m, perLine);
        Assert.Equal(31.00m, flatOverBasket);
        // The customer would have been overcharged by exactly the VAT on the exempt line.
        Assert.Equal(15.50m, flatOverBasket - perLine);
    }

    [Fact]
    public void Vat_is_rounded_half_away_from_zero_to_the_cent()
    {
        // 0.155 x 33.33 = 5.16615. Banker's rounding would disagree on the halfway cases, and the
        // total charged has to match what is printed to the cent.
        Assert.Equal(5.17m, Settings().VatOn(33.33m, "O01"));
        Assert.Equal(0.02m, Settings().VatOn(0.10m, "O01"));
    }

    [Fact]
    public void A_settings_object_with_no_rates_configured_still_charges_the_standard_rate()
    {
        // The shipped default before any RatesByTaxCode entries exist. Nothing should suddenly become
        // zero-rated because configuration is missing.
        var bare = new TaxSettings();

        Assert.Equal(0.155m, bare.RateFor("O0"));
        Assert.Equal(0.155m, bare.RateFor(null));
    }
}
