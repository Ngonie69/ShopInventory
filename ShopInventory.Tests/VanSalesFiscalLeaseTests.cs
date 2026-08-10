using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Features.VanSalesCompatibility;
using ShopInventory.Services.Fiscalisation;

namespace ShopInventory.Tests;

/// <summary>
/// The lease tells a van handset what tax to put on receipts it will sign hours later, with no way to
/// ask again. So the rule this suite holds in place is that a missing fact is left out, never filled in:
/// an item the lease omits is refused at the till by name, whereas an item given a plausible-looking
/// default prints a receipt with the wrong tax that nothing downstream can detect.
/// </summary>
public sealed class VanSalesFiscalLeaseTests
{
    private static readonly DateTime Now = new(2026, 8, 10, 12, 0, 0, DateTimeKind.Unspecified);

    private const int StandardTaxId = 517;
    private const int ZeroRatedTaxId = 2;

    [Fact]
    public void BuildTaxes_KeepsARateThatIsInForce()
    {
        var config = Config(
            Tax(StandardTaxId, 15.5m, "O8", validFrom: Now.AddYears(-1)));

        var taxes = VanSalesFiscalLeaseMapper.BuildTaxes(config, Now);

        var tax = Assert.Single(taxes);
        Assert.Equal(StandardTaxId, tax.TaxId);
        Assert.Equal(15.5m, tax.Percent);
        Assert.Equal("O8", tax.Code);
    }

    /// <summary>
    /// An expired rate left in the lease is one the handset would go on signing with for days, because
    /// offline it has nothing to check against.
    /// </summary>
    [Fact]
    public void BuildTaxes_DropsARateThatHasExpired()
    {
        var config = Config(
            Tax(StandardTaxId, 14.5m, "O7", validFrom: Now.AddYears(-2), validTill: Now.AddDays(-1)));

        Assert.Empty(VanSalesFiscalLeaseMapper.BuildTaxes(config, Now));
    }

    [Fact]
    public void BuildTaxes_DropsARateThatHasNotStartedYet()
    {
        var config = Config(Tax(StandardTaxId, 16m, "O9", validFrom: Now.AddDays(1)));

        Assert.Empty(VanSalesFiscalLeaseMapper.BuildTaxes(config, Now));
    }

    /// <summary>A rate change is two entries under one id; the one in force now is the one that ships.</summary>
    [Fact]
    public void BuildTaxes_TakesTheCurrentEntryWhenARateHasChanged()
    {
        var config = Config(
            Tax(StandardTaxId, 14.5m, "O7", validFrom: Now.AddYears(-3), validTill: Now.AddYears(-1)),
            Tax(StandardTaxId, 15.5m, "O8", validFrom: Now.AddYears(-1)));

        var tax = Assert.Single(VanSalesFiscalLeaseMapper.BuildTaxes(config, Now));
        Assert.Equal(15.5m, tax.Percent);
    }

    /// <summary>Null percent is exempt and must survive: null and 0 are signed differently.</summary>
    [Fact]
    public void BuildTaxes_KeepsAnExemptRatesNullPercentage()
    {
        var config = Config(Tax(9, null, "Exempt", validFrom: Now.AddYears(-1)));

        Assert.Null(Assert.Single(VanSalesFiscalLeaseMapper.BuildTaxes(config, Now)).Percent);
    }

    [Fact]
    public void BuildItemTaxes_MapsAnItemThroughItsVatGroup()
    {
        var settings = Settings(mappings: new() { ["O8"] = StandardTaxId });

        var itemTaxes = VanSalesFiscalLeaseMapper.BuildItemTaxes(
            new Dictionary<string, string> { ["CHE011"] = "O8" }, settings, Taxes(), out var unmapped);

        var item = Assert.Single(itemTaxes);
        Assert.Equal("CHE011", item.ItemCode);
        Assert.Equal(StandardTaxId, item.TaxId);
        Assert.Equal("0406", item.HsCode);
        Assert.Empty(unmapped);
    }

    [Fact]
    public void BuildItemTaxes_FallsBackToTheConfiguredDefaultTaxId()
    {
        var settings = Settings(mappings: [], defaultTaxId: StandardTaxId);

        var itemTaxes = VanSalesFiscalLeaseMapper.BuildItemTaxes(
            new Dictionary<string, string> { ["CHE011"] = "O8" }, settings, Taxes(), out var unmapped);

        Assert.Equal(StandardTaxId, Assert.Single(itemTaxes).TaxId);
        Assert.Empty(unmapped);
    }

    /// <summary>
    /// With no mapping and no default there is no honest answer, so the item is dropped and its group
    /// reported. The handset then names it at the till instead of taxing it at a guess.
    /// </summary>
    [Fact]
    public void BuildItemTaxes_OmitsAnItemWithNoMappingAndNoDefault()
    {
        var settings = Settings(mappings: [], defaultTaxId: 0);

        var itemTaxes = VanSalesFiscalLeaseMapper.BuildItemTaxes(
            new Dictionary<string, string> { ["CHE011"] = "O8" }, settings, Taxes(), out var unmapped);

        Assert.Empty(itemTaxes);
        Assert.Equal("O8", Assert.Single(unmapped));
    }

    /// <summary>
    /// A tax id that is mapped but that the device's own configuration does not carry cannot be signed
    /// against — the receipt would cite a rate the lease never stated.
    /// </summary>
    [Fact]
    public void BuildItemTaxes_OmitsAnItemWhoseTaxIdTheDeviceDoesNotCarry()
    {
        var settings = Settings(mappings: new() { ["O8"] = 999 });

        var itemTaxes = VanSalesFiscalLeaseMapper.BuildItemTaxes(
            new Dictionary<string, string> { ["CHE011"] = "O8" }, settings, Taxes(), out var unmapped);

        Assert.Empty(itemTaxes);
        Assert.Equal("O8", Assert.Single(unmapped));
    }

    /// <summary>One bad group must not cost the rest of the catalogue its offline trading.</summary>
    [Fact]
    public void BuildItemTaxes_KeepsTheMappableItemsWhenOneGroupIsUnmapped()
    {
        var settings = Settings(mappings: new() { ["O8"] = StandardTaxId, ["Z"] = ZeroRatedTaxId });

        var itemTaxes = VanSalesFiscalLeaseMapper.BuildItemTaxes(
            new Dictionary<string, string>
            {
                ["CHE011"] = "O8",
                ["MLK001"] = "Z",
                ["ODD001"] = "QQ"
            },
            settings,
            Taxes(),
            out var unmapped);

        Assert.Equal(2, itemTaxes.Count);
        Assert.Equal("QQ", Assert.Single(unmapped));
    }

    /// <summary>
    /// SAP's VAT groups are not consistently cased across the item master, and a case-sensitive lookup
    /// would silently strand whole groups.
    /// </summary>
    [Fact]
    public void BuildItemTaxes_MatchesTheVatGroupWithoutRegardToCaseOrSpace()
    {
        var settings = Settings(mappings: new() { ["O8"] = StandardTaxId });

        var itemTaxes = VanSalesFiscalLeaseMapper.BuildItemTaxes(
            new Dictionary<string, string> { ["CHE011"] = " o8 " }, settings, Taxes(), out _);

        Assert.Equal(StandardTaxId, Assert.Single(itemTaxes).TaxId);
    }

    [Fact]
    public void BuildItemTaxes_SkipsAnItemSapLeftWithoutAVatGroup()
    {
        var settings = Settings(mappings: new() { ["O8"] = StandardTaxId }, defaultTaxId: StandardTaxId);

        var itemTaxes = VanSalesFiscalLeaseMapper.BuildItemTaxes(
            new Dictionary<string, string> { ["CHE011"] = "  " }, settings, Taxes(), out _);

        Assert.Empty(itemTaxes);
    }

    private static List<VanSalesFiscalTaxDto> Taxes() =>
    [
        new() { TaxId = StandardTaxId, Percent = 15.5m, Code = "O8" },
        new() { TaxId = ZeroRatedTaxId, Percent = 0m, Code = "Z" }
    ];

    private static FiscalisationSettings Settings(
        Dictionary<string, int>? mappings = null,
        int defaultTaxId = 0) => new()
        {
            TaxIdMappings = new Dictionary<string, int>(
                mappings ?? [], StringComparer.OrdinalIgnoreCase),
            DefaultTaxId = defaultTaxId,
            DefaultHsCode = "0406"
        };

    private static FiscalConfigApiResponse Config(params FiscalTaxDto[] taxes) => new()
    {
        DeviceSerialNo = "VAN006",
        QrUrl = "https://fdms.zimra.co.zw/",
        TaxPayerDayMaxHrs = 24,
        ApplicableTaxes = [.. taxes]
    };

    private static FiscalTaxDto Tax(
        int taxId,
        decimal? percent,
        string name,
        DateTime validFrom,
        DateTime? validTill = null) => new()
        {
            TaxID = taxId,
            TaxPercent = percent,
            TaxName = name,
            TaxValidFrom = validFrom,
            TaxValidTill = validTill
        };
}
