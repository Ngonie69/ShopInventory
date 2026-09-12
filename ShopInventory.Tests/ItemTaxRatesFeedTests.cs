using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.DesktopIntegration.Queries.GetItemTaxRates;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// Covers the rates a till is served, and what it is told when there are none.
///
/// The till prices its own basket: it shows VAT on screen and prints VAT on a receipt before the
/// platform has seen the sale. It used to answer that from a hard-coded list of exempt item codes
/// that did not match the item master, so a sale of a zero-rated item was rung up at 15.5% and
/// invoiced at zero. This route is the other half of <see cref="ItemTaxGroupStampingTests"/>: both
/// read one table, so the basket and the invoice cannot disagree.
/// </summary>
public sealed class ItemTaxRatesFeedTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public ItemTaxRatesFeedTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite(_connection)
                .Options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    /// <summary>The rates as shipped: standard everywhere, zero for the exempt group.</summary>
    private static TaxSettings Rates() => new()
    {
        VatRate = 0.155m,
        RatesByTaxCode = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
        {
            ["O01"] = 0.155m,
            ["O0"] = 0.0m,
        }
    };

    private async Task StoreAsync(params (string Item, string Group)[] rows)
    {
        foreach (var (item, group) in rows)
        {
            _context.SapItemTaxGroups.Add(new SapItemTaxGroupEntity
            {
                ItemCode = item,
                VatGroup = group,
                ResolvedAtUtc = new DateTime(2026, 9, 12, 3, 45, 0, DateTimeKind.Utc),
            });
        }

        await _context.SaveChangesAsync();
    }

    private async Task<ItemTaxRatesResult> ReadAsync(TaxSettings? tax = null)
    {
        var handler = new GetItemTaxRatesHandler(
            _context,
            Options.Create(tax ?? Rates()),
            NullLogger<GetItemTaxRatesHandler>.Instance);

        var result = await handler.Handle(new GetItemTaxRatesQuery(), CancellationToken.None);

        Assert.False(result.IsError);
        return result.Value;
    }

    [Fact]
    public async Task An_exempt_item_is_served_at_zero()
    {
        await StoreAsync(("CHE011", "O0"));

        var rates = await ReadAsync();

        var item = Assert.Single(rates.Items);
        Assert.Equal("CHE011", item.ItemCode);
        Assert.Equal("O0", item.VatGroup);
        Assert.Equal(0m, item.Rate);
    }

    [Fact]
    public async Task A_standard_rated_item_is_served_at_the_standard_rate()
    {
        // SUP001 and LAC002 are the two the till's own list called exempt and the item master does
        // not. They are the reason a till's total and SAP's differed on every sale of either.
        await StoreAsync(("SUP001", "O01"), ("LAC002", "O01"));

        var rates = await ReadAsync();

        Assert.All(rates.Items, item => Assert.Equal(0.155m, item.Rate));
    }

    [Fact]
    public async Task A_group_with_no_rate_of_its_own_falls_to_the_standard_rate()
    {
        // O3 and O4 are real groups SAP rates at zero and deliberately left out of the rate table,
        // because neither has an FDMS tax id yet. Charging zero while declaring the standard rate is
        // worse than the overcharge, so they stay standard-rated until somebody maps them.
        await StoreAsync(("RMA003", "O3"));

        var rates = await ReadAsync();

        Assert.Equal(0.155m, Assert.Single(rates.Items).Rate);
    }

    [Fact]
    public async Task The_default_rate_is_what_an_unnamed_item_is_charged()
    {
        await StoreAsync(("CHE011", "O0"));

        var rates = await ReadAsync();

        // The till applies this to anything this answer does not name, which is exactly what
        // CreateDesktopSaleHandler does with a line whose item has no stored group.
        Assert.Equal(0.155m, rates.DefaultRate);
    }

    [Fact]
    public async Task An_empty_table_is_answered_rather_than_refused()
    {
        // The warm job has not run. A till that treated this as an error and threw its stored rates
        // away would go straight back to charging 15.5% on zero-rated goods.
        var rates = await ReadAsync();

        Assert.Empty(rates.Items);
        Assert.Null(rates.ResolvedAtUtc);
        Assert.Equal(0.155m, rates.DefaultRate);
    }

    [Fact]
    public void The_wire_names_are_the_ones_the_till_reads()
    {
        // The till deserialises this body into KefShop/Models/ItemTaxRatesMDL.cs, and the same
        // literal is parsed back there by KefShop.Tests/Utilities/TaxRatesWireTests.cs. Two repos
        // cannot share a type, so they share a string: rename a property on either side and one of
        // the two tests fails, rather than a till quietly charging the standard rate on everything
        // because "rate" arrived under a name it does not read.
        //
        // Built here rather than read back through the handler on purpose. Sqlite hands a DateTime
        // back as Unspecified and serialises it without the Z, where the Npgsql column this really
        // reads is timestamptz and does carry one -- so a round trip would pin the test database's
        // habits instead of the contract. The names are the contract, and they are the same either way.
        var result = new ItemTaxRatesResult(
            DefaultRate: 0.155m,
            ResolvedAtUtc: new DateTime(2026, 9, 12, 3, 45, 0, DateTimeKind.Utc),
            Items: [new ItemTaxRateDto("CHE011", "O0", 0.0m)]);

        var wire = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(
            """
            {"defaultRate":0.155,"resolvedAtUtc":"2026-09-12T03:45:00Z","items":[{"itemCode":"CHE011","vatGroup":"O0","rate":0.0}]}
            """,
            wire);
    }

    [Fact]
    public async Task The_age_served_is_that_of_the_newest_row()
    {
        await StoreAsync(("CHE011", "O0"));
        _context.SapItemTaxGroups.Add(new SapItemTaxGroupEntity
        {
            ItemCode = "SUP001",
            VatGroup = "O01",
            ResolvedAtUtc = new DateTime(2026, 9, 13, 3, 45, 0, DateTimeKind.Utc),
        });
        await _context.SaveChangesAsync();

        var rates = await ReadAsync();

        Assert.Equal(new DateTime(2026, 9, 13, 3, 45, 0, DateTimeKind.Utc), rates.ResolvedAtUtc);
    }
}
