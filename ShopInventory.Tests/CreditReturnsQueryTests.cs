using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Stock;
using ShopInventory.Data;
using ShopInventory.Features.DesktopIntegration.Queries.GetCreditReturns;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// A till can read which credit notes put stock back on its ledger, credit by credit.
/// </summary>
/// <remarks>
/// A credit returns its units the moment ZIMRA has it. The local stock route shows only the figure that
/// leaves behind, so on the till's stock pages the units arrived with no document and were listed as a
/// correction to explain. These rows are the document.
/// </remarks>
public sealed class CreditReturnsQueryTests : IDisposable
{
    private const string Shop = "KEFSHOP";

    private static readonly DateTime SaleDay = new(2026, 10, 12);
    private static readonly DateTime ReturnDay = new(2026, 10, 13);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public CreditReturnsQueryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new SnapshotSqliteContext(
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

    [Fact]
    public async Task A_return_names_its_credit_the_sale_it_reverses_and_the_item_that_came_back()
    {
        var sale = await AddSale("KEF-FAC-1", ("FET-500", "Feta 500g"));
        var credit = await AddCredit(sale, "Damaged in the fridge");
        await AddReturn(credit.Number, "FET-500", Shop, 2m, ReturnDay, new DateTime(2026, 10, 13, 9, 40, 0));

        var row = Assert.Single((await Handle(ReturnDay, ReturnDay)).Value);

        Assert.Equal($"CN{sale.Id}", row.CreditNoteNumber);
        Assert.Equal($"INV{sale.Id}", row.SaleNumber);
        Assert.Equal("KEF-FAC-1", row.SaleReference);
        Assert.Equal(SaleDay, row.SaleDate);
        Assert.Equal("FET-500", row.ItemCode);
        Assert.Equal("Feta 500g", row.ItemDescription);
        Assert.Equal(2m, row.Quantity);
        Assert.Equal("Damaged in the fridge", row.Reason);
        // The day the stock figure rose, which is the day of the credit and not of the sale.
        Assert.Equal(ReturnDay, row.LedgerDay);
        Assert.Equal(DateTimeKind.Utc, row.ReturnedAt.Kind);
    }

    [Fact]
    public async Task Only_this_shops_credit_returns_for_the_days_asked_are_listed_in_the_order_they_happened()
    {
        var sale = await AddSale("KEF-FAC-1", ("A", "A"), ("B", "B"), ("C", "C"), ("D", "D"));
        var credit = await AddCredit(sale, "Returned");

        await AddReturn(credit.Number, "B", Shop, 1m, ReturnDay, new DateTime(2026, 10, 13, 14, 0, 0));
        await AddReturn(credit.Number, "A", Shop, 1m, ReturnDay, new DateTime(2026, 10, 13, 8, 0, 0));
        await AddReturn(credit.Number, "C", Shop, 1m, ReturnDay.AddDays(2), new DateTime(2026, 10, 15, 8, 0, 0));
        await AddReturn(credit.Number, "D", "KEFGRS", 1m, ReturnDay, new DateTime(2026, 10, 13, 8, 0, 0));

        // A sale's claim handed back because SAP refused it is also a Release, and is no credit note.
        await AddMovement(StockMovementKinds.Release, "desktop-sale:KEF-FAC-9", "A", Shop, 5m, ReturnDay, new DateTime(2026, 10, 13, 9, 0, 0));
        // And a sale taking stock is not a return at all.
        await AddMovement(StockMovementKinds.Commit, "desktop-sale:KEF-FAC-10", "A", Shop, -3m, ReturnDay, new DateTime(2026, 10, 13, 10, 0, 0));

        var result = await Handle(ReturnDay, ReturnDay.AddDays(1));

        Assert.Equal(["A", "B"], result.Value.Select(row => row.ItemCode));
    }

    [Fact]
    public async Task A_second_credit_on_a_sale_is_listed_under_its_own_number()
    {
        var sale = await AddSale("KEF-FAC-1", ("FET-500", "Feta 500g"));
        var first = await AddCredit(sale, "One back");
        var second = await AddCredit(sale, "Another back");
        await AddReturn(first.Number, "FET-500", Shop, 1m, ReturnDay, new DateTime(2026, 10, 13, 9, 0, 0));
        await AddReturn(second.Number, "FET-500", Shop, 1m, ReturnDay, new DateTime(2026, 10, 13, 11, 0, 0));

        var result = await Handle(ReturnDay, ReturnDay);

        Assert.Equal([$"CN{sale.Id}", $"CN{sale.Id}-2"], result.Value.Select(row => row.CreditNoteNumber));
    }

    [Fact]
    public async Task A_return_whose_credit_is_gone_is_still_listed_because_it_still_moved_the_stock()
    {
        await AddReturn("DCN-orphan", "FET-500", Shop, 2m, ReturnDay, new DateTime(2026, 10, 13, 9, 0, 0));

        var row = Assert.Single((await Handle(ReturnDay, ReturnDay)).Value);

        Assert.Equal("DCN-orphan", row.CreditNoteNumber);
        Assert.Null(row.SaleNumber);
        Assert.Equal(2m, row.Quantity);
    }

    [Fact]
    public void The_key_a_return_is_journalled_under_is_the_one_already_in_the_journal()
    {
        // Returns were journalled under this literal before it had a name. A different prefix would
        // read every one of them as something other than a credit note.
        Assert.Equal("desktop-credit:DCN-abc", DesktopCreditStockReturn.DocumentKey("DCN-abc"));
        Assert.Equal("DCN-abc", DesktopCreditStockReturn.CreditNumberOf("desktop-credit:DCN-abc"));
        Assert.Null(DesktopCreditStockReturn.CreditNumberOf("desktop-sale:KEF-FAC-9"));
        Assert.Null(DesktopCreditStockReturn.CreditNumberOf(null));
    }

    [Fact]
    public async Task A_backwards_range_is_refused()
    {
        Assert.True((await Handle(ReturnDay, ReturnDay.AddDays(-1))).IsError);
    }

    [Fact]
    public async Task A_range_longer_than_a_quarter_is_refused()
    {
        Assert.True((await Handle(new DateTime(2026, 1, 1), new DateTime(2026, 9, 1))).IsError);
    }

    private Task<ErrorOr.ErrorOr<List<CreditReturnDto>>> Handle(DateTime from, DateTime to) =>
        new GetCreditReturnsHandler(_context).Handle(new GetCreditReturnsQuery(Shop, from, to), default);

    private async Task<DesktopSaleEntity> AddSale(string reference, params (string Item, string Name)[] lines)
    {
        var sale = new DesktopSaleEntity
        {
            ExternalReferenceId = reference,
            SourceSystem = "KefalosShopTill",
            CardCode = Shop,
            WarehouseCode = Shop,
            DocDate = SaleDay,
            TotalAmount = 100m,
            VatAmount = 13.42m,
            AmountPaid = 100m,
            Currency = "USD",
            CreatedAt = new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc),
            Lines = lines
                .Select((line, index) => new DesktopSaleLineEntity
                {
                    LineNum = index,
                    ItemCode = line.Item,
                    ItemDescription = line.Name,
                    Quantity = 4m,
                    UnitPrice = 10m,
                    LineTotal = 40m,
                    WarehouseCode = Shop,
                })
                .ToList(),
        };
        _context.DesktopSales.Add(sale);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return sale;
    }

    private async Task<DesktopCreditNoteEntity> AddCredit(DesktopSaleEntity sale, string reason)
    {
        var credit = new DesktopCreditNoteEntity
        {
            Id = Guid.NewGuid(),
            SaleId = sale.Id,
            RequestKey = Guid.NewGuid().ToString("N"),
            Number = $"DCN-{Guid.NewGuid():N}",
            OriginalFiscalNumber = sale.ExternalReferenceId,
            Reason = reason,
            Currency = "USD",
            Amount = 23.10m,
            Status = DesktopCreditStatuses.Fiscalised,
            UnitsReturnedToLedger = true,
            CreatedAtUtc = new DateTime(2026, 10, 13, 9, 30, 0, DateTimeKind.Utc)
                .AddMinutes(_context.DesktopCreditNotes.Count()),
        };
        _context.DesktopCreditNotes.Add(credit);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        return credit;
    }

    private Task AddReturn(
        string creditNumber, string item, string warehouse, decimal quantity, DateTime ledgerDay, DateTime occurredAt) =>
        AddMovement(
            StockMovementKinds.Release, DesktopCreditStockReturn.DocumentKey(creditNumber),
            item, warehouse, quantity, ledgerDay, occurredAt);

    private async Task AddMovement(
        string kind, string documentKey, string item, string warehouse, decimal quantity, DateTime ledgerDay, DateTime occurredAt)
    {
        _context.StockMovements.Add(new StockMovementEntity
        {
            LedgerDay = ledgerDay,
            Kind = kind,
            DocumentKey = documentKey,
            ItemCode = item,
            WarehouseCode = warehouse,
            Quantity = quantity,
            BalanceAfter = 10m,
            Reference = "test",
            OccurredAt = occurredAt,
        });

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }
}
