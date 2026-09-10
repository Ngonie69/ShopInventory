using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.DesktopIntegration.Commands.CreateDesktopSale;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Covers what a till line is taxed at, and the store that answers it.
///
/// A till sends no tax code — it has no source for one, and a tax code arriving from a client is a
/// tax code a client can get wrong. So every line fell to the standard rate: a zero-rated item was
/// charged 15.5% the customer did not owe, and the receipt declared to ZIMRA said the same, because
/// the fiscal payload picks its FDMS tax id from the very same field.
///
/// The store behind it is warmed nightly rather than read per sale, so most of these are about what
/// happens when the warm is stale, empty or has never run — cases where the wrong answer is worse
/// than none.
/// </summary>
public sealed class ItemTaxGroupStampingTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public ItemTaxGroupStampingTests()
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

    private static Dictionary<string, string> Master(params (string Item, string Group)[] rows) =>
        rows.ToDictionary(r => r.Item, r => r.Group, StringComparer.OrdinalIgnoreCase);

    private Task<SapItemTaxGroupWarmJob.WarmOutcome> WarmAsync(Dictionary<string, string> master) =>
        SapItemTaxGroupWarmJob.ApplyAsync(
            _context, master, DateTime.UtcNow, NullLogger.Instance, CancellationToken.None);

    // ---- what a line is charged -------------------------------------------------------------

    [Fact]
    public void A_line_is_taxed_at_its_items_own_vat_group()
    {
        var code = CreateDesktopSaleHandler.TaxCodeFor(
            new CreateDesktopSaleLineRequest { ItemCode = "CHE011" },
            Master(("CHE011", "O0")));

        Assert.Equal("O0", code);
    }

    [Fact]
    public void The_item_master_wins_over_whatever_the_request_asked_for()
    {
        // Nothing sends a tax code today. The day something does, it does not get to decide what an
        // item is taxed at - that is master data, and a client that could set it could under-declare
        // a sale to ZIMRA.
        var code = CreateDesktopSaleHandler.TaxCodeFor(
            new CreateDesktopSaleLineRequest { ItemCode = "CHE011", TaxCode = "O01" },
            Master(("CHE011", "O0")));

        Assert.Equal("O0", code);
    }

    [Fact]
    public void An_item_with_no_stored_group_keeps_the_behaviour_it_had_before()
    {
        // Null, so TaxSettings falls to the standard rate exactly as it did before this lookup
        // existed. Wrong for a zero-rated item, but it is the old wrong rather than a new one, and
        // the sale is never refused over a tax lookup with a customer at the counter.
        var code = CreateDesktopSaleHandler.TaxCodeFor(
            new CreateDesktopSaleLineRequest { ItemCode = "NRI049" },
            Master(("CHE011", "O0")));

        Assert.Null(code);
    }

    [Fact]
    public void A_zero_rated_line_is_charged_no_vat_once_it_is_stamped()
    {
        // The point of the whole change, in money. Same basket, before and after.
        var tax = new TaxSettings
        {
            VatRate = 0.155m,
            RatesByTaxCode = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
            {
                ["O01"] = 0.155m,
                ["O0"] = 0m
            }
        };

        Assert.Equal(1.55m, tax.VatOn(10.00m, null));   // what it used to charge
        Assert.Equal(0m, tax.VatOn(10.00m, "O0"));      // what it charges now
    }

    // ---- the store behind it ----------------------------------------------------------------

    [Fact]
    public async Task An_empty_answer_from_sap_does_not_empty_the_table()
    {
        // The failure that would be caused by the job meant to prevent it: taking "no rows" as "no
        // item has a tax group" puts every till back on the standard rate for everything.
        await WarmAsync(Master(("CHE011", "O0")));

        var outcome = await WarmAsync(new Dictionary<string, string>());

        Assert.Equal(0, outcome.Added);
        Assert.Equal("O0", _context.SapItemTaxGroups.Single().VatGroup);
    }

    [Fact]
    public async Task An_item_that_changes_vat_group_is_updated_rather_than_duplicated()
    {
        await WarmAsync(Master(("CHE011", "O01")));

        var outcome = await WarmAsync(Master(("CHE011", "O0")));

        Assert.Equal(1, outcome.Changed);
        Assert.Equal(0, outcome.Added);

        var row = _context.SapItemTaxGroups.Single();
        Assert.Equal("O0", row.VatGroup);
    }

    [Fact]
    public async Task An_item_missing_from_a_later_read_keeps_the_group_it_had()
    {
        // A sweep cut short mid-way returns a partial answer. Deleting what it did not mention would
        // silently move those items to the standard rate.
        await WarmAsync(Master(("CHE011", "O0"), ("NRI049", "O01")));

        await WarmAsync(Master(("CHE011", "O0")));

        Assert.Equal(2, _context.SapItemTaxGroups.Count());
        Assert.Equal("O01", _context.SapItemTaxGroups.Single(r => r.ItemCode == "NRI049").VatGroup);
    }

    [Fact]
    public async Task Rerunning_the_warm_over_the_same_answer_adds_nothing()
    {
        await WarmAsync(Master(("CHE011", "O0")));

        var outcome = await WarmAsync(Master(("CHE011", "O0")));

        Assert.Equal(0, outcome.Added);
        Assert.Equal(0, outcome.Changed);
        Assert.Single(_context.SapItemTaxGroups);
    }
}
