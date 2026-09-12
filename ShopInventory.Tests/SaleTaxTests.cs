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
    public void A_mixed_basket_is_taxed_at_each_lines_rate_not_flat_across_it()
    {
        // THE case. Two lines, one zero-rated. Flat-rating the basket charges 15.5% on all of it.
        var tax = Settings();

        (decimal NetAmount, string? TaxCode)[] lines =
        [
            (100m, "O01"),
            (100m, "O0"),
        ];

        var byRate = tax.VatOnBasket(lines);
        var flatOverBasket = Math.Round(lines.Sum(l => l.NetAmount) * tax.VatRate, 2);

        Assert.Equal(15.50m, byRate);
        Assert.Equal(31.00m, flatOverBasket);
        // The customer would have been overcharged by exactly the VAT on the exempt line.
        Assert.Equal(15.50m, flatOverBasket - byRate);
    }

    [Fact]
    public void A_basket_rounds_its_vat_once_per_rate_not_once_per_line()
    {
        // Sale 5 at Graniteside, 11 September: 17, 20 and 20 units at net 6.25, sent with no tax codes.
        // Line by line that is 16.47 + 19.38 + 19.38 = 55.23, and the sale was totalled 411.48. The till
        // charged 411.47, SAP invoiced VAT of 55.22, and ZIMRA holds 55.22: 356.25 x 15.5%, rounded once.
        var tax = Settings();

        (decimal NetAmount, string? TaxCode)[] lines =
        [
            (106.25m, null),
            (125.00m, null),
            (125.00m, null),
        ];

        Assert.Equal(55.23m, lines.Sum(l => tax.VatOn(l.NetAmount, l.TaxCode)));
        Assert.Equal(55.22m, tax.VatOnBasket(lines));
    }

    [Fact]
    public void A_line_with_no_code_is_rounded_together_with_the_standard_rated_lines()
    {
        // Both are declared under the default, standard-rated tax id, so FDMS files them as one group.
        // 15.5% of 0.10 is 0.0155: 0.02 twice line by line, but 0.031 once over the pair.
        Assert.Equal(0.03m, Settings().VatOnBasket([(0.10m, null), (0.10m, "O01")]));
    }

    [Fact]
    public void The_vat_charged_is_the_vat_the_fiscal_device_works_back_from_the_total()
    {
        // FDMS is handed the tax-inclusive total and derives the group's tax from it, rounded once — the
        // formula FiscalReceiptDerivation.Derive mirrors. Rounding once over a group's net lands on that
        // same cent for every amount, checked here to $2,000.00. A single line cannot tell once-per-rate
        // from once-per-line; the baskets above are what pin that.
        var tax = Settings();

        for (var cents = 1; cents <= 200_000; cents++)
        {
            var net = cents / 100m;
            var vat = tax.VatOnBasket([(net, "O01")]);
            var filed = Math.Round((net + vat) * 15.5m / 115.5m, 2, MidpointRounding.AwayFromZero);

            Assert.Equal(filed, vat);
        }
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
