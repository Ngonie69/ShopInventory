using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Pricing;
using ShopInventory.DTOs;
using ShopInventory.Data;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Covers splitting a business partner special price back into the price list price plus a
/// discount percent.
/// </summary>
/// <remarks>
/// Merging the special price over the price list price used to leave a document line showing a net
/// price against an empty Discount % column, while SAP showed the same line as 0.70 less 5.714286%.
/// The split has to reproduce SAP's number exactly, and must never move what the customer pays.
/// </remarks>
public sealed class SpecialPriceDiscountTests
{
    [Fact]
    public void Discount_matches_what_sap_shows()
    {
        // VHU002 for ABS006: SAP's Sales Quotation shows 0.700000 less 5.714286% = 0.66.
        Assert.Equal(5.714286m, SpecialPriceDiscount.CalculatePercent(0.70m, 0.66m));
    }

    [Fact]
    public void Discount_is_exact_when_it_divides_cleanly()
    {
        Assert.Equal(10m, SpecialPriceDiscount.CalculatePercent(12.50m, 11.25m));
    }

    [Fact]
    public void Special_price_equal_to_list_price_is_not_a_discount()
    {
        Assert.Null(SpecialPriceDiscount.CalculatePercent(12.50m, 12.50m));
    }

    [Fact]
    public void Special_price_above_list_price_is_not_reported_as_a_discount()
    {
        // A negative discount would price the line above what the customer agreed to pay.
        Assert.Null(SpecialPriceDiscount.CalculatePercent(12.50m, 13.00m));
    }

    [Fact]
    public void Zero_special_price_is_not_reported_as_a_full_discount()
    {
        // A zero row means missing data, not free stock.
        Assert.Null(SpecialPriceDiscount.CalculatePercent(12.50m, 0m));
    }

    [Fact]
    public void Missing_list_price_leaves_the_special_price_alone()
    {
        Assert.Null(SpecialPriceDiscount.CalculatePercent(0m, 12.50m));
    }
}

/// <summary>
/// Covers the locally stored pricing merge — the path used when SAP cannot be reached — carrying
/// the price list price and the discount alongside the price the customer pays.
/// </summary>
public sealed class LocalBusinessPartnerPricingMergeTests : IDisposable
{
    private const string CardCode = "ABS006";
    private const int PriceListNum = 1;

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly LocalPriceCatalogService _service;

    public LocalBusinessPartnerPricingMergeTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .Options);
        _context.Database.EnsureCreated();

        _service = new LocalPriceCatalogService(_context);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Special_price_is_split_into_a_list_price_and_a_discount()
    {
        await GivenCustomer();
        await GivenPriceListPrice("VHU002", 0.70m);
        await GivenSpecialPrice("VHU002", 0.66m);

        var price = await GetPrice("VHU002");

        // Price stays the net price so nothing that bills off it changes.
        Assert.Equal(0.66m, price.Price);
        Assert.Equal(0.70m, price.PriceBeforeDiscount);
        Assert.Equal(5.714286m, price.DiscountPercent);
    }

    [Fact]
    public async Task Price_list_price_carries_no_discount()
    {
        await GivenCustomer();
        await GivenPriceListPrice("VHU002", 0.70m);

        var price = await GetPrice("VHU002");

        Assert.Equal(0.70m, price.Price);
        Assert.Null(price.PriceBeforeDiscount);
        Assert.Null(price.DiscountPercent);
    }

    [Fact]
    public async Task Special_price_without_a_price_list_price_carries_no_discount()
    {
        // Nothing to discount from, so the special price is the unit price.
        await GivenCustomer();
        await GivenSpecialPrice("VHU002", 0.66m);

        var price = await GetPrice("VHU002");

        Assert.Equal(0.66m, price.Price);
        Assert.Null(price.PriceBeforeDiscount);
        Assert.Null(price.DiscountPercent);
    }

    private async Task<ItemPriceByListDto> GetPrice(string itemCode)
    {
        var pricing = await _service.GetBusinessPartnerPricingAsync(CardCode);

        Assert.NotNull(pricing);
        return Assert.Single(
            pricing.Prices.Prices!,
            price => string.Equals(price.ItemCode, itemCode, StringComparison.OrdinalIgnoreCase));
    }

    private async Task GivenCustomer()
    {
        _context.BusinessPartnerPriceProfiles.Add(new BusinessPartnerPriceProfileEntity
        {
            CardCode = CardCode,
            CardName = "Absolute Refrigeration",
            Currency = "USD",
            IsActive = true,
            PriceListNum = PriceListNum,
            SyncedFromSAP = true
        });

        await _context.SaveChangesAsync();
    }

    private async Task GivenPriceListPrice(string itemCode, decimal price)
    {
        _context.ItemPrices.Add(new ItemPriceEntity
        {
            ItemCode = itemCode,
            Price = price,
            PriceList = PriceListNum,
            Currency = "USD",
            IsActive = true,
            SyncedFromSAP = true
        });

        await _context.SaveChangesAsync();
    }

    private async Task GivenSpecialPrice(string itemCode, decimal price)
    {
        _context.BusinessPartnerSpecialPrices.Add(new BusinessPartnerSpecialPriceEntity
        {
            CardCode = CardCode,
            ItemCode = itemCode,
            Price = price,
            IsActive = true,
            SyncedFromSAP = true
        });

        await _context.SaveChangesAsync();
    }
}
