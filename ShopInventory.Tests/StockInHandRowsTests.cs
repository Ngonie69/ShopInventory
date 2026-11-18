using ShopInventory.Web.Common;
using ShopInventory.Web.Components.Pages;
using ShopInventory.Web.Models;

namespace ShopInventory.Tests;

/// <summary>
/// The arithmetic /stock-in-hand and the shop stock dashboard draw: rows for a moment, an item's
/// movements, the day the shop's stock opens, and one row per document on "Today in the shop".
/// </summary>
public sealed class StockInHandRowsTests
{
    private static DateTime Utc(int d, int h, int m = 0) => new(2026, 11, d, h, m, 0, DateTimeKind.Utc);

    [Fact]
    public void A_past_moment_is_set_against_the_ledgers_now_with_names_and_units_from_the_live_stock()
    {
        var then = new StockAsAtResponse
        {
            Recorded = true,
            Items = [new() { ItemCode = "FET", ItemName = "Feta snapshot name", OnHand = 10m }, new() { ItemCode = "GOU", OnHand = 1.4m }]
        };
        var now = new StockAsAtResponse
        {
            Recorded = true,
            Items = [new() { ItemCode = "FET", OnHand = 4m }, new() { ItemCode = "ICE", OnHand = 12m }]
        };
        var live = new List<ProductDto>
        {
            new() { ItemCode = "FET", ItemName = "Feta 400g", UoM = "ea", QuantityInStock = 99m },
            new() { ItemCode = "ICE", ItemName = "Ice cream 2L", UoM = "ea", QuantityInStock = 99m }
        };

        var rows = StockInHandRows.FromLedger(then, now, live).ToDictionary(r => r.Code);

        Assert.Equal(3, rows.Count);
        Assert.Equal("Feta 400g", rows["FET"].Name);
        Assert.Equal("ea", rows["FET"].UoM);
        Assert.Equal(10m, rows["FET"].Then);
        // Now comes from the ledger, not SAP's 99, so the change is what the ledger moved.
        Assert.Equal(4m, rows["FET"].Now);
        Assert.Equal(-6m, rows["FET"].Change);
        // In stock then, gone from the ledger now.
        Assert.Equal(1.4m, rows["GOU"].Then);
        Assert.Equal(0m, rows["GOU"].Now);
        // Not there then, arrived since.
        Assert.Equal(0m, rows["ICE"].Then);
        Assert.Equal(12m, rows["ICE"].Change);
    }

    [Fact]
    public void When_the_ledger_cannot_describe_now_SAPs_live_figure_stands_in()
    {
        var then = new StockAsAtResponse { Recorded = true, Items = [new() { ItemCode = "FET", OnHand = 10m }] };
        var now = new StockAsAtResponse { Recorded = false };
        var live = new List<ProductDto> { new() { ItemCode = "FET", QuantityInStock = 7m } };

        Assert.Equal(7m, Assert.Single(StockInHandRows.FromLedger(then, now, live)).Now);
    }

    [Fact]
    public void Now_reads_free_to_sell_as_on_hand_less_committed_and_can_go_below_zero()
    {
        var row = Assert.Single(StockInHandRows.FromLive(
            [new ProductDto { ItemCode = "MOZ", ItemName = "Mozzarella", QuantityInStock = 9m, QuantityCommitted = 12m }]));

        Assert.Equal(-3m, row.Free);
    }

    [Fact]
    public void An_items_movements_restart_at_each_mornings_count_from_SAP()
    {
        var response = new StockMovementsResponse
        {
            Movements =
            [
                new() { OccurredAtUtc = Utc(17, 12), Kind = StockLedgerKinds.Commit, Quantity = -3m, BalanceAfter = 17m, Reference = "till sale" },
                new() { OccurredAtUtc = Utc(18, 6), Kind = StockLedgerKinds.Transfer, Quantity = 10m, BalanceAfter = 28m, Reference = "TR 4471" }
            ],
            Openings = [new() { TakenAtUtc = Utc(18, 5, 2), Quantity = 18m }]
        };

        var ledger = StockInHandRows.Ledger(response);

        Assert.Equal(["Sold", "Morning count from SAP", "Transferred in"], ledger.Select(e => e.Label));
        Assert.Equal([17m, 18m, 28m], ledger.Select(e => e.Balance));
        Assert.True(ledger[1].IsOpening);
        Assert.Null(ledger[1].Quantity);
    }

    [Fact]
    public void A_window_starting_mid_day_starts_from_the_first_movements_balance_before_it()
    {
        var response = new StockMovementsResponse
        {
            Movements = [new() { OccurredAtUtc = Utc(18, 7), Kind = StockLedgerKinds.Commit, Quantity = -2m, BalanceAfter = 16m }]
        };

        Assert.Equal(18m, StockInHandRows.StartBalance(response, fallback: 99m));
        Assert.Equal(5m, StockInHandRows.StartBalance(new StockMovementsResponse(), fallback: 5m));
    }

    [Theory]
    // 06:59 CAT is still yesterday's stock day; 07:00 opens today's.
    [InlineData(4, 59, 17)]
    [InlineData(5, 0, 18)]
    [InlineData(21, 30, 18)]
    public void The_shops_stock_day_opens_at_seven_CAT(int hourUtc, int minuteUtc, int openingDay)
    {
        var opening = StockDayClock.OpeningUtc(Utc(18, hourUtc, minuteUtc));

        Assert.Equal(new DateTime(2026, 11, openingDay, 5, 0, 0), opening);
        Assert.Equal(DateTimeKind.Utc, opening.Kind);
    }

    [Fact]
    public void Today_in_the_shop_is_one_row_per_document_not_one_per_item()
    {
        var movements = new List<StockLedgerMovement>
        {
            new() { OccurredAtUtc = Utc(18, 6), Kind = StockLedgerKinds.Commit, DocumentKey = "sale:1", ItemCode = "A", Quantity = -1m, Reference = "till sale 1" },
            new() { OccurredAtUtc = Utc(18, 6), Kind = StockLedgerKinds.Commit, DocumentKey = "sale:1", ItemCode = "B", Quantity = -2m, Reference = "till sale 1" },
            new() { OccurredAtUtc = Utc(18, 7), Kind = StockLedgerKinds.Transfer, DocumentKey = "tr:9", ItemCode = "A", Quantity = 12m, Reference = "TR 9" },
            // Corrections carry no key; two in one minute are one row, a later one is another.
            new() { OccurredAtUtc = Utc(18, 8), Kind = StockLedgerKinds.Reconciliation, ItemCode = "A", Quantity = -1m, Reference = "hourly SAP comparison" },
            new() { OccurredAtUtc = Utc(18, 8), Kind = StockLedgerKinds.Reconciliation, ItemCode = "B", Quantity = 1m, Reference = "hourly SAP comparison" },
            new() { OccurredAtUtc = Utc(18, 9), Kind = StockLedgerKinds.Reconciliation, ItemCode = "A", Quantity = 2m, Reference = "hourly SAP comparison" }
        };

        var rows = ShopStockDashboard.GroupMoves(movements);

        Assert.Equal(4, rows.Count);
        Assert.Equal(["SAP correction", "SAP correction", "Transferred in", "Sold"], rows.Select(r => r.Label));
        Assert.Equal(2, rows[^1].Items);
        Assert.Equal(-3m, rows[^1].Net);
    }
}
