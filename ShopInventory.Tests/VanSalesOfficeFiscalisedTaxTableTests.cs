using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Features.VanSalesCompatibility;

namespace ShopInventory.Tests;

/// <summary>
/// The tax table issued to a van handset when the office does the fiscalising.
///
/// Under REVMax the handset holds no signing authority, so nothing it is sent can produce a wrong
/// receipt — but it still prices every basket, and these rates are bound into every money figure on
/// every screen. The failure this suite exists to prevent is therefore a pricing one and it is silent:
/// with no table the handset falls back to a single standard percentage, so a zero-rated line is shown
/// to the rep, and charged to the customer, with 15.5% added to something that carries no VAT.
/// </summary>
public sealed class VanSalesOfficeFiscalisedTaxTableTests
{
    private const int RevmaxStandardTaxId = 1;
    private const int RevmaxZeroRatedTaxId = 2;

    /// <summary>Production's own pairing, as shipped in appsettings.json.</summary>
    private static Dictionary<string, int> ProductionTaxIds() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["O01"] = RevmaxStandardTaxId,
        ["O8"] = RevmaxStandardTaxId,
        ["O0"] = RevmaxZeroRatedTaxId
    };

    private static TaxSettings ProductionRates() => new()
    {
        VatRate = 0.155m,
        RatesByTaxCode = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
        {
            ["O01"] = 0.155m,
            ["O8"] = 0.155m,
            ["O0"] = 0.0m
        }
    };

    [Fact]
    public void A_zero_rated_group_is_issued_at_zero_and_not_at_the_standard_rate()
    {
        var taxes = VanSalesFiscalLeaseMapper.BuildProviderTaxes(
            ProductionTaxIds(), RevmaxStandardTaxId, ProductionRates(), out var conflicts);

        Assert.Empty(conflicts);

        var zeroRated = Assert.Single(taxes, tax => tax.TaxId == RevmaxZeroRatedTaxId);

        // 0 and null are not the same answer: null is untaxed, 0 is zero-rated, and the handset's
        // fallback percentage is what this exists to displace.
        Assert.Equal(0m, zeroRated.Percent);
        Assert.NotNull(zeroRated.Percent);
    }

    [Fact]
    public void The_rate_is_stated_as_a_percentage_not_as_a_fraction()
    {
        var taxes = VanSalesFiscalLeaseMapper.BuildProviderTaxes(
            ProductionTaxIds(), RevmaxStandardTaxId, ProductionRates(), out _);

        var standard = Assert.Single(taxes, tax => tax.TaxId == RevmaxStandardTaxId);

        // The handset quotes SalesTax.FallbackPercent = 15.5m, and FDMS reports 15.5 rather than 0.155.
        // A fraction here would price the whole catalogue at 0.155% and look almost right on a small
        // total.
        Assert.Equal(15.5m, standard.Percent);
    }

    [Fact]
    public void Two_groups_sharing_one_id_at_one_rate_is_not_a_conflict()
    {
        // O01 and O8 are the USD and ZiG standard-rated groups. They are the ordinary case, not a fault.
        var taxes = VanSalesFiscalLeaseMapper.BuildProviderTaxes(
            ProductionTaxIds(), RevmaxStandardTaxId, ProductionRates(), out var conflicts);

        Assert.Empty(conflicts);
        Assert.Equal(2, taxes.Count);
    }

    [Fact]
    public void An_id_two_groups_rate_differently_is_left_out_rather_than_guessed()
    {
        var taxIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["O01"] = RevmaxStandardTaxId,
            ["O8"] = RevmaxStandardTaxId
        };

        var rates = new TaxSettings
        {
            VatRate = 0.155m,
            RatesByTaxCode = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
            {
                ["O01"] = 0.155m,
                ["O8"] = 0.14m
            }
        };

        var taxes = VanSalesFiscalLeaseMapper.BuildProviderTaxes(
            taxIds, defaultTaxId: 0, rates, out var conflicts);

        // Configuration disagreeing with itself. Reported and dropped: the handset then refuses those
        // items by name, rather than pricing half the catalogue at whichever was enumerated first.
        Assert.Equal(RevmaxStandardTaxId, Assert.Single(conflicts));
        Assert.Empty(taxes);
    }

    [Fact]
    public void The_fallback_id_is_in_the_table_so_unlisted_items_are_not_dropped()
    {
        var taxIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["O0"] = RevmaxZeroRatedTaxId
        };

        var taxes = VanSalesFiscalLeaseMapper.BuildProviderTaxes(
            taxIds, RevmaxStandardTaxId, ProductionRates(), out _);

        // Nothing maps to the standard id here, but an item whose group is unlisted still resolves to
        // it. Leaving it out of the table would make BuildItemTaxes drop every such item.
        var fallback = Assert.Single(taxes, tax => tax.TaxId == RevmaxStandardTaxId);
        Assert.Equal(15.5m, fallback.Percent);
    }

    [Fact]
    public void An_item_is_mapped_through_its_group_to_the_providers_own_id()
    {
        var taxes = VanSalesFiscalLeaseMapper.BuildProviderTaxes(
            ProductionTaxIds(), RevmaxStandardTaxId, ProductionRates(), out _);

        var itemTaxes = VanSalesFiscalLeaseMapper.BuildItemTaxes(
            new Dictionary<string, string> { ["CHEESE01"] = "O01", ["BREAD01"] = "O0" },
            ProductionTaxIds(),
            RevmaxStandardTaxId,
            defaultHsCode: "0406",
            taxes,
            out var unmapped);

        Assert.Empty(unmapped);

        Assert.Equal(
            RevmaxStandardTaxId,
            Assert.Single(itemTaxes, item => item.ItemCode == "CHEESE01").TaxId);

        // The one that matters: this item must not arrive carrying the standard id.
        Assert.Equal(
            RevmaxZeroRatedTaxId,
            Assert.Single(itemTaxes, item => item.ItemCode == "BREAD01").TaxId);
    }

    [Fact]
    public void The_platform_overload_still_maps_through_the_platforms_own_ids()
    {
        // REVMax ids and FDMS ids are different numbering schemes over the identical VAT groups, and
        // interchanging them misdeclares the receipt. The generalised overload must not have quietly
        // pointed the platform path at REVMax's table.
        var settings = new FiscalisationSettings
        {
            DefaultTaxId = 517,
            DefaultHsCode = "0406",
            TaxIdMappings = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["O01"] = 517,
                ["O0"] = 2
            }
        };

        var taxes = new List<VanSalesFiscalTaxDto>
        {
            new() { TaxId = 517, Percent = 15.5m, Code = "O01" },
            new() { TaxId = 2, Percent = 0m, Code = "O0" }
        };

        var itemTaxes = VanSalesFiscalLeaseMapper.BuildItemTaxes(
            new Dictionary<string, string> { ["CHEESE01"] = "O01" },
            settings,
            taxes,
            out var unmapped);

        Assert.Empty(unmapped);
        Assert.Equal(517, Assert.Single(itemTaxes).TaxId);
    }
}
