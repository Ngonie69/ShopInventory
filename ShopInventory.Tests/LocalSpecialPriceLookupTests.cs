using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Covers which stored special prices count as applying today.
/// </summary>
/// <remarks>
/// This is the query approval falls back to when SAP cannot be reached, so a wrong answer here
/// prices a customer's document. Getting it wrong in the "too strict" direction silently
/// overcharges them — the exact failure that put order 2209 into SAP at list price on
/// 2026-07-27 — so the validity-window boundaries are pinned deliberately.
/// </remarks>
public sealed class LocalSpecialPriceLookupTests : IDisposable
{
    private const string CardCode = "TMP119";

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly LocalPriceCatalogService _service;

    public LocalSpecialPriceLookupTests()
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
    public async Task Price_inside_its_validity_window_is_returned()
    {
        await GivenSpecialPrice("YOG149", 12.50m, validFrom: Today.AddDays(-30), validTo: Today.AddDays(30));

        var prices = await _service.GetActiveSpecialPricesAsync(CardCode);

        Assert.Equal(12.50m, prices["YOG149"]);
    }

    [Fact]
    public async Task Open_ended_price_is_returned()
    {
        // Most negotiated prices carry no end date at all.
        await GivenSpecialPrice("YOG149", 12.50m, validFrom: null, validTo: null);

        var prices = await _service.GetActiveSpecialPricesAsync(CardCode);

        Assert.Equal(12.50m, prices["YOG149"]);
    }

    [Fact]
    public async Task Price_expiring_today_still_applies()
    {
        // The boundary that matters: excluding it would overcharge on its final day.
        await GivenSpecialPrice("YOG149", 12.50m, validFrom: Today.AddDays(-30), validTo: Today);

        var prices = await _service.GetActiveSpecialPricesAsync(CardCode);

        Assert.Equal(12.50m, prices["YOG149"]);
    }

    [Fact]
    public async Task Price_starting_today_already_applies()
    {
        await GivenSpecialPrice("YOG149", 12.50m, validFrom: Today, validTo: Today.AddDays(30));

        var prices = await _service.GetActiveSpecialPricesAsync(CardCode);

        Assert.Equal(12.50m, prices["YOG149"]);
    }

    [Fact]
    public async Task Expired_price_is_excluded()
    {
        await GivenSpecialPrice("YOG149", 12.50m, validFrom: Today.AddDays(-30), validTo: Today.AddDays(-1));

        var prices = await _service.GetActiveSpecialPricesAsync(CardCode);

        Assert.Empty(prices);
    }

    [Fact]
    public async Task Price_not_yet_in_force_is_excluded()
    {
        await GivenSpecialPrice("YOG149", 12.50m, validFrom: Today.AddDays(1), validTo: Today.AddDays(30));

        var prices = await _service.GetActiveSpecialPricesAsync(CardCode);

        Assert.Empty(prices);
    }

    [Fact]
    public async Task Inactive_price_is_excluded()
    {
        await GivenSpecialPrice("YOG149", 12.50m, isActive: false);

        var prices = await _service.GetActiveSpecialPricesAsync(CardCode);

        Assert.Empty(prices);
    }

    [Fact]
    public async Task Price_not_synced_from_sap_is_excluded()
    {
        await GivenSpecialPrice("YOG149", 12.50m, syncedFromSap: false);

        var prices = await _service.GetActiveSpecialPricesAsync(CardCode);

        Assert.Empty(prices);
    }

    [Fact]
    public async Task Another_customers_price_is_excluded()
    {
        await GivenSpecialPrice("YOG149", 12.50m, cardCode: "TMP123");

        var prices = await _service.GetActiveSpecialPricesAsync(CardCode);

        Assert.Empty(prices);
    }

    [Fact]
    public async Task Item_codes_narrow_the_result()
    {
        await GivenSpecialPrice("YOG149", 12.50m);
        await GivenSpecialPrice("CHE001", 8.00m);

        var prices = await _service.GetActiveSpecialPricesAsync(
            CardCode,
            ["YOG149"]);

        Assert.Equal(12.50m, Assert.Single(prices).Value);
    }

    [Fact]
    public async Task Lookup_is_case_insensitive_on_item_code()
    {
        // Callers index this dictionary with whatever case the order line carried.
        await GivenSpecialPrice("YOG149", 12.50m);

        var prices = await _service.GetActiveSpecialPricesAsync(CardCode);

        Assert.Equal(12.50m, prices["yog149"]);
    }

    [Fact]
    public async Task Zero_price_is_excluded()
    {
        // Applying 0.00 would give the goods away; a zero row means missing data, not free stock.
        await GivenSpecialPrice("YOG149", 0m);

        var prices = await _service.GetActiveSpecialPricesAsync(CardCode);

        Assert.Empty(prices);
    }

    [Fact]
    public async Task Blank_card_code_returns_nothing_without_querying()
    {
        await GivenSpecialPrice("YOG149", 12.50m);

        Assert.Empty(await _service.GetActiveSpecialPricesAsync("   "));
    }

    private static DateTime Today => DateTime.UtcNow.Date;

    private async Task GivenSpecialPrice(
        string itemCode,
        decimal price,
        string? cardCode = null,
        DateTime? validFrom = null,
        DateTime? validTo = null,
        bool isActive = true,
        bool syncedFromSap = true)
    {
        _context.BusinessPartnerSpecialPrices.Add(new BusinessPartnerSpecialPriceEntity
        {
            CardCode = cardCode ?? CardCode,
            ItemCode = itemCode,
            Price = price,
            ValidFrom = validFrom,
            ValidTo = validTo,
            IsActive = isActive,
            SyncedFromSAP = syncedFromSap
        });

        await _context.SaveChangesAsync();
    }
}
