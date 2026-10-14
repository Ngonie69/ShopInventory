using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.Features.DesktopCreditNotes;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// Pins the one read every Shop &amp; Vending sales report takes its credits from: which sales in a
/// report's scope were credited back, for how much, and how much of each item.
/// </summary>
/// <remarks>
/// The reports totalled each sale at what it was rung up for even after the office had credited it, so
/// what this answers is what they take off. The rule for what a credit is worth is
/// <see cref="SaleCredits"/>'s and is pinned there; these are about a report's scope and a line's value.
/// </remarks>
public sealed class CreditedSalesTests : IDisposable
{
    private static readonly DateTime Day = new(2026, 9, 25);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public CreditedSalesTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Only_the_sales_something_was_credited_on_are_read()
    {
        var credited = await AddSale("TILL-1");
        await AddSale("TILL-2");
        await SaleCreditSeed.TillCreditAsync(_context, credited.Id, 23.10m, lines: (ReceiptLine: 2, Quantity: 2m, UnitPrice: 11.55m));

        var sale = Assert.Single(await Read());

        Assert.Equal(credited.Id, sale.SaleId);
        Assert.Equal(161.70m, sale.TotalAmount);
        Assert.Equal(23.10m, sale.CreditedAmount);
        Assert.Equal([$"CN{credited.Id}"], sale.CreditNoteNumbers);
        Assert.False(sale.IsFullyCredited);
    }

    [Fact]
    public async Task A_credited_line_is_valued_on_the_lines_own_basis_not_the_credits()
    {
        // The credit is filed tax-inclusive: two at 11.55. The line sold four for 40.00 before tax, so
        // what comes off the item's line value is half of it — 20.00, not 23.10.
        var sale = await AddSale("TILL-1");
        await SaleCreditSeed.TillCreditAsync(_context, sale.Id, 23.10m, lines: (ReceiptLine: 2, Quantity: 2m, UnitPrice: 11.55m));

        var line = Assert.Single(Assert.Single(await Read()).Lines);

        Assert.Equal(new CreditedSaleLine("FET-500", 2m, 20m), line);
    }

    [Fact]
    public async Task A_memo_against_the_sales_own_invoice_is_read_with_the_line_it_reverses()
    {
        var sale = await AddSale("TILL-1", sapDocEntry: 2001);
        await SaleCreditSeed.MemoAsync(_context, 900, 55294, lines: (BaseEntry: 2001, BaseLine: 0, ItemCode: "MOZ-1KG", LineTotal: 30m, Vat: 4.65m));

        var credited = Assert.Single(await Read());

        Assert.Equal(34.65m, credited.CreditedAmount);
        Assert.Equal(["55294"], credited.CreditNoteNumbers);
        Assert.Equal(new CreditedSaleLine("MOZ-1KG", 3m, 30m), Assert.Single(credited.Lines));
    }

    [Fact]
    public async Task A_credit_never_gives_back_more_of_a_line_than_it_sold()
    {
        // A memo's quantity is worked out from its money. One keyed for more than the line was worth
        // must not take the item below nothing.
        var sale = await AddSale("TILL-1", sapDocEntry: 2001);
        await SaleCreditSeed.MemoAsync(_context, 900, 55294, lines: (BaseEntry: 2001, BaseLine: 1, ItemCode: "FET-500", LineTotal: 60m, Vat: 9.30m));

        var line = Assert.Single(Assert.Single(await Read()).Lines);

        Assert.Equal(new CreditedSaleLine("FET-500", 4m, 40m), line);
    }

    [Fact]
    public async Task A_credit_ZIMRA_has_not_accepted_and_a_cancelled_memo_leave_the_sale_out()
    {
        var refused = await AddSale("TILL-1");
        var cancelled = await AddSale("TILL-2", sapDocEntry: 2001);
        await SaleCreditSeed.TillCreditAsync(_context, refused.Id, 10m, DesktopCreditStatuses.Rejected);
        await SaleCreditSeed.MemoAsync(_context, 900, 55294, isCancelled: true, (BaseEntry: 2001, BaseLine: 0, ItemCode: "MOZ-1KG", LineTotal: 30m, Vat: 4.65m));

        Assert.Empty(await Read());
    }

    [Fact]
    public async Task A_sale_outside_the_reports_scope_is_not_read_however_it_was_credited()
    {
        var inScope = await AddSale("TILL-1");
        var another = await AddSale("TILL-2", day: Day.AddDays(1));
        await SaleCreditSeed.TillCreditAsync(_context, inScope.Id, 5m);
        await SaleCreditSeed.TillCreditAsync(_context, another.Id, 7m);

        var credited = await CreditedSales.InAsync(
            _context, _context.DesktopSales.AsNoTracking().Where(sale => sale.DocDate == Day), CancellationToken.None);

        Assert.Equal(inScope.Id, Assert.Single(credited).SaleId);
    }

    [Fact]
    public async Task A_sale_credited_in_full_says_so_and_carries_what_a_report_groups_by()
    {
        var sale = await AddSale("TILL-1");
        await SaleCreditSeed.TillCreditAsync(_context, sale.Id, 161.70m);

        var credited = Assert.Single(await Read());

        Assert.True(credited.IsFullyCredited);
        Assert.Equal(Day, credited.DocDate);
        Assert.Equal("USD", credited.Currency);
        Assert.Equal("KEFSHOP", credited.WarehouseCode);
        Assert.Equal("FARM-BP", credited.CardCode);
        Assert.Equal(SaleSourceSystems.ShopTill, credited.SourceSystem);
        Assert.Equal("Cash", credited.PaymentMethod);
        // No plan names a line, so the money counts and no item can show it.
        Assert.Empty(credited.Lines);
    }

    private Task<List<CreditedSale>> Read() =>
        CreditedSales.InAsync(_context, _context.DesktopSales.AsNoTracking(), CancellationToken.None);

    /// <summary>A till sale of ten mozzarella and four feta at 10.00 before tax: 140.00, and 161.70 with VAT.</summary>
    private async Task<DesktopSaleEntity> AddSale(string reference, int? sapDocEntry = null, DateTime? day = null)
    {
        var sale = new DesktopSaleEntity
        {
            ExternalReferenceId = reference,
            SourceSystem = SaleSourceSystems.ShopTill,
            CardCode = "FARM-BP",
            WarehouseCode = "KEFSHOP",
            DocDate = day ?? Day,
            // Lines are held before tax, 100.00 + 40.00, and the total carries 15.5% on top — so a test
            // that set a tax-inclusive credit against a before-tax line would get the wrong figure.
            TotalAmount = 161.70m,
            VatAmount = 21.70m,
            AmountPaid = 161.70m,
            Currency = "USD",
            PaymentMethod = "Cash",
            SapDocEntry = sapDocEntry,
            CreatedAt = DateTime.SpecifyKind((day ?? Day).AddHours(9), DateTimeKind.Utc),
            Lines =
            [
                new DesktopSaleLineEntity { LineNum = 1, ItemCode = "MOZ-1KG", ItemDescription = "Mozzarella", Quantity = 10m, UnitPrice = 10m, LineTotal = 100m, WarehouseCode = "KEFSHOP" },
                new DesktopSaleLineEntity { LineNum = 2, ItemCode = "FET-500", ItemDescription = "Feta", Quantity = 4m, UnitPrice = 10m, LineTotal = 40m, WarehouseCode = "KEFSHOP" },
            ],
        };
        _context.DesktopSales.Add(sale);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return sale;
    }
}
