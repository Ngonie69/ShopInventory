using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Pricing;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Covers SAP's "Period and Volume Discounts": special price rows filed under <c>*</c> and a price
/// list number, which apply to every customer on that price list.
/// </summary>
/// <remarks>
/// Only rows under the customer's own card code were ever read, so these were never applied. On
/// 2026-11-26 Create Invoice priced MUE005 for COR007 (price list 46) at 0.41 while SAP's own price
/// engine (CompanyService_GetItemPrice) charged 0.41 less 23.978537%, from the <c>*46</c> row.
/// </remarks>
public sealed class SpecialPriceScopeTests
{
    [Fact]
    public void A_price_lists_discounts_are_filed_under_a_star_and_its_number()
    {
        Assert.Equal("*46", SpecialPriceScope.PriceListCardCode(46));
        Assert.Null(SpecialPriceScope.PriceListCardCode(null));
        Assert.Null(SpecialPriceScope.PriceListCardCode(0));
    }

    [Fact]
    public void A_customer_is_priced_from_its_own_rows_and_its_price_lists()
    {
        Assert.Equal(["COR007", "*46"], SpecialPriceScope.CardCodes("COR007", 46));
        Assert.Equal(["COR007"], SpecialPriceScope.CardCodes("COR007", null));
    }

    [Fact]
    public void A_period_discount_prices_an_item_the_customer_has_no_special_price_for()
    {
        var prices = SpecialPriceScope.Resolve("COR007", [("*46", "MUE005", 0.311688m)]);

        Assert.Equal(0.311688m, prices["MUE005"]);
    }

    [Fact]
    public void The_customers_own_special_price_beats_its_price_lists_period_discount()
    {
        // SAP's order: special prices for the business partner first, then period and volume
        // discounts. Read in either order, the customer's own row must win.
        var prices = SpecialPriceScope.Resolve("COR007",
        [
            ("*46", "MUE005", 0.311688m),
            ("COR007", "MUE005", 0.30m),
            ("*46", "VHU002", 0.60m)
        ]);

        Assert.Equal(0.30m, prices["MUE005"]);
        Assert.Equal(0.60m, prices["VHU002"]);
    }

    [Fact]
    public void The_customer_is_matched_whatever_the_case()
    {
        var prices = SpecialPriceScope.Resolve("cor007",
        [
            ("COR007", "MUE005", 0.30m),
            ("*46", "MUE005", 0.311688m)
        ]);

        Assert.Equal(0.30m, prices["mue005"]);
    }

    [Fact]
    public void Sap_is_asked_for_the_customer_and_its_price_list_in_one_filter()
    {
        var filter = SAPServiceLayerClient.BuildSpecialPriceFilter(["COR007", "*46"], ["MUE005"]);

        Assert.Equal("(CardCode eq 'COR007' or CardCode eq '*46') and (ItemCode eq 'MUE005')", filter);
    }

    [Fact]
    public void Without_a_price_list_the_filter_is_the_customer_alone()
    {
        Assert.Equal(
            "CardCode eq 'COR007'",
            SAPServiceLayerClient.BuildSpecialPriceFilter(["COR007"], []));
    }

    [Fact]
    public void The_period_row_in_force_prices_the_line()
    {
        Assert.True(SAPServiceLayerClient.TryExtractCurrentSpecialPrice(Mue005OnList46(), new DateTime(2026, 11, 26), out var price));
        Assert.Equal(0.311688m, price);
    }

    [Fact]
    public void Before_its_period_starts_the_header_price_applies()
    {
        // The header is the list price itself, so no discount follows from it.
        Assert.True(SAPServiceLayerClient.TryExtractCurrentSpecialPrice(Mue005OnList46(), new DateTime(2026, 11, 25), out var price));
        Assert.Equal(0.41m, price);
    }

    [Fact]
    public void The_discount_matches_what_sap_charges()
    {
        Assert.Equal(23.978537m, SpecialPriceDiscount.CalculatePercent(0.41m, 0.311688m));
    }

    // SAP's SpecialPrices row for MUE005 on price list 46, as the Service Layer returned it on 2026-11-26.
    private static JsonElement Mue005OnList46() => JsonDocument.Parse("""
        {
          "ItemCode": "MUE005",
          "CardCode": "*46",
          "Price": 0.41,
          "Currency": "USD",
          "DiscountPercent": 0.0,
          "PriceListNum": 46,
          "AutoUpdate": "tYES",
          "SourcePrice": "sc_PrimaryCurrency",
          "Valid": "tYES",
          "ValidFrom": "2026-11-24T00:00:00Z",
          "ValidTo": "2027-03-31T00:00:00Z",
          "SpecialPriceDataAreas": [
            {
              "PriceCurrency": "USD",
              "AutoUpdate": "tYES",
              "Dateto": "2027-03-31T00:00:00Z",
              "Discount": 23.978537,
              "SpecialPrice": 0.311688,
              "DateFrom": "2026-11-26T00:00:00Z",
              "BPCode": "*46",
              "PriceListNo": 46,
              "ItemNo": "MUE005",
              "RowNumber": 0,
              "SpecialPriceQuantityAreas": []
            }
          ]
        }
        """).RootElement;
}

/// <summary>
/// The same rule on the locally synced catalogue, which prices invoices and answers whenever live
/// SAP pricing is slow. The sync already stores the <c>*46</c> rows; they were never looked up.
/// </summary>
public sealed class LocalPeriodVolumeDiscountTests : IDisposable
{
    private const string CardCode = "COR007";
    private const int PriceListNum = 46;

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;
    private readonly LocalPriceCatalogService _service;

    public LocalPeriodVolumeDiscountTests()
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
    public async Task The_price_lists_period_discount_prices_the_customer()
    {
        await GivenCustomer();
        await GivenPriceListPrice("MUE005", 0.41m);
        await GivenSpecialPrice("*46", "MUE005", 0.311688m);

        var price = await GetPrice("MUE005");

        Assert.Equal(0.311688m, price.Price);
        Assert.Equal(0.41m, price.PriceBeforeDiscount);
        Assert.Equal(23.978537m, price.DiscountPercent);
    }

    [Fact]
    public async Task The_customers_own_special_price_wins()
    {
        await GivenCustomer();
        await GivenPriceListPrice("MUE005", 0.41m);
        await GivenSpecialPrice("*46", "MUE005", 0.311688m);
        await GivenSpecialPrice(CardCode, "MUE005", 0.35m);

        Assert.Equal(0.35m, (await GetPrice("MUE005")).Price);
    }

    [Fact]
    public async Task An_expired_customer_price_gives_way_to_the_period_discount()
    {
        await GivenCustomer();
        await GivenPriceListPrice("MUE005", 0.41m);
        await GivenSpecialPrice("*46", "MUE005", 0.311688m);
        await GivenSpecialPrice(CardCode, "MUE005", 0.35m, validTo: DateTime.UtcNow.Date.AddDays(-1));

        Assert.Equal(0.311688m, (await GetPrice("MUE005")).Price);
    }

    [Fact]
    public async Task Another_price_lists_discount_is_not_applied()
    {
        await GivenCustomer();
        await GivenPriceListPrice("MUE005", 0.41m);
        await GivenSpecialPrice("*47", "MUE005", 0.25m);

        var price = await GetPrice("MUE005");

        Assert.Equal(0.41m, price.Price);
        Assert.Null(price.DiscountPercent);
    }

    [Fact]
    public async Task The_standalone_lookup_includes_the_period_discount()
    {
        // What sales order approval falls back to when SAP's special price read does not finish.
        await GivenCustomer();
        await GivenSpecialPrice("*46", "MUE005", 0.311688m);
        await GivenSpecialPrice("*47", "VHU002", 0.25m);

        var prices = await _service.GetActiveSpecialPricesAsync(CardCode);

        Assert.Equal(0.311688m, Assert.Single(prices).Value);
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
            CardName = "Graniteside Kefalos Shop USD POS",
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
            ItemName = itemCode,
            PriceList = PriceListNum,
            PriceListName = "USD POS",
            Price = price,
            Currency = "USD",
            IsActive = true,
            SyncedFromSAP = true
        });

        await _context.SaveChangesAsync();
    }

    private async Task GivenSpecialPrice(string cardCode, string itemCode, decimal price, DateTime? validTo = null)
    {
        _context.BusinessPartnerSpecialPrices.Add(new BusinessPartnerSpecialPriceEntity
        {
            CardCode = cardCode,
            ItemCode = itemCode,
            Price = price,
            ValidTo = validTo,
            IsActive = true,
            SyncedFromSAP = true
        });

        await _context.SaveChangesAsync();
    }
}

/// <summary>
/// Covers the unit price an invoice line is posted at once the catalogue has priced it.
/// </summary>
public sealed class DocumentLineUnitPriceTests
{
    private static readonly ItemPriceByListDto Mue005 = new()
    {
        ItemCode = "MUE005",
        Price = 0.311688m,
        PriceBeforeDiscount = 0.41m,
        DiscountPercent = 23.978537m
    };

    [Fact]
    public void A_line_carrying_the_special_discount_posts_the_list_price()
    {
        // Create Invoice sends 0.41 less 23.978537%. Posting the 0.311688 net price under that
        // discount took it off twice — 0.237 to SAP for a 0.311688 item.
        Assert.Equal(0.41m, DocumentLineUnitPrice.For(Mue005, 23.978537m));
    }

    [Fact]
    public void A_line_without_a_discount_posts_the_net_special_price()
    {
        // A caller that sends no discount must still be charged the special price.
        Assert.Equal(0.311688m, DocumentLineUnitPrice.For(Mue005, null));
        Assert.Equal(0.311688m, DocumentLineUnitPrice.For(Mue005, 0m));
    }

    [Fact]
    public void A_list_priced_item_posts_its_price_whatever_discount_the_line_carries()
    {
        var listPriced = new ItemPriceByListDto { ItemCode = "VHU002", Price = 0.70m };

        Assert.Equal(0.70m, DocumentLineUnitPrice.For(listPriced, 5m));
        Assert.Equal(0.70m, DocumentLineUnitPrice.For(listPriced, null));
    }
}
