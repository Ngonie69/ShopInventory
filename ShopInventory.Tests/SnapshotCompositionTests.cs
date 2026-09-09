using ShopInventory.Common.Stock;
using ShopInventory.DTOs;
using ShopInventory.Models;

namespace ShopInventory.Tests;

/// <summary>
/// How the morning snapshot is built, and which day it belongs to.
/// </summary>
/// <remarks>
/// Three defects, all of which made the ledger disagree with SAP from the moment it was written:
/// items SAP does not manage by batch had no row at all and were unsellable from a till; batch
/// quantities are gross, so the snapshot started every day more optimistic than the live figure the
/// web path checks against; and the day a snapshot belonged to was resolved five hours before the
/// snapshot existed.
/// </remarks>
public sealed class SnapshotCompositionTests
{
    // ---------------------------------------------------------------
    // Which day's snapshot is in force
    // ---------------------------------------------------------------

    private static readonly TimeSpan SevenAm = new(7, 0, 0);

    /// <summary>CAT is UTC+2 and does not observe daylight saving.</summary>
    private static DateTime Cat(int year, int month, int day, int hour, int minute) =>
        new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Utc).AddHours(-2);

    [Theory]
    // Through the trading day itself.
    [InlineData(2026, 9, 8, 7, 0, "2026-09-08")]
    [InlineData(2026, 9, 8, 12, 0, "2026-09-08")]
    [InlineData(2026, 9, 8, 23, 59, "2026-09-08")]
    // Past midnight, still selling against Tuesday morning's figures. UTC dating got this right and
    // a naive conversion to the CAT calendar day is what breaks it.
    [InlineData(2026, 9, 9, 0, 30, "2026-09-08")]
    [InlineData(2026, 9, 9, 1, 59, "2026-09-08")]
    // The window UTC dating got wrong: the UTC date rolls at 02:00 CAT, five hours before the fetch
    // that produces the next snapshot, so every caller looked for one that did not exist yet.
    [InlineData(2026, 9, 9, 2, 0, "2026-09-08")]
    [InlineData(2026, 9, 9, 4, 0, "2026-09-08")]
    [InlineData(2026, 9, 9, 6, 59, "2026-09-08")]
    // And the roll itself, at the fetch.
    [InlineData(2026, 9, 9, 7, 0, "2026-09-09")]
    public void The_ledger_day_rolls_at_the_fetch_not_at_a_midnight(
        int year, int month, int day, int hour, int minute, string expected)
    {
        var resolved = StockLedgerDay.Resolve(Cat(year, month, day, hour, minute), SevenAm);

        Assert.Equal(expected, resolved.ToString("yyyy-MM-dd"));
    }

    [Fact]
    public void A_mistyped_fetch_time_falls_back_rather_than_stopping_the_tills()
    {
        Assert.Equal(StockLedgerDay.DefaultFetchTime, StockLedgerDay.ParseFetchTime("not a time"));
        Assert.Equal(StockLedgerDay.DefaultFetchTime, StockLedgerDay.ParseFetchTime(null));
        Assert.Equal(new TimeSpan(6, 30, 0), StockLedgerDay.ParseFetchTime("06:30"));
    }

    // ---------------------------------------------------------------
    // Items SAP does not manage by batch
    // ---------------------------------------------------------------

    [Fact]
    public void An_item_with_no_batches_still_gets_a_row()
    {
        // The snapshot was built from batch rows alone, so an item like this summed to zero and no
        // till could ever sell it — silently, for as long as the item existed.
        var composed = SnapshotComposer.Compose(
            batches: [],
            warehouseStock: [Stock("CON020", inStock: 40, committed: 0)]);

        var row = Assert.Single(composed.Rows);
        Assert.Equal("CON020", row.ItemCode);
        Assert.Null(row.BatchNumber);
        Assert.Equal(40m, row.AvailableQuantity);
    }

    [Fact]
    public void An_item_with_nothing_issuable_gets_no_row()
    {
        var composed = SnapshotComposer.Compose(
            batches: [],
            warehouseStock: [Stock("CON020", inStock: 5, committed: 5)]);

        Assert.Empty(composed.Rows);
    }

    // ---------------------------------------------------------------
    // Commitments
    // ---------------------------------------------------------------

    [Fact]
    public void Committed_stock_is_not_offered_to_a_till()
    {
        // Ledger A checks InStock less Committed. Ledger B used the gross batch quantity, so the two
        // disagreed by the whole open-commitment balance before a single sale had been made.
        var composed = SnapshotComposer.Compose(
            batches: [Batch("CHE011", "B-1", quantity: 10, expiry: "2026-12-01")],
            warehouseStock: [Stock("CHE011", inStock: 10, committed: 4)]);

        Assert.Equal(6m, composed.Rows.Sum(row => row.AvailableQuantity));

        // The original figure is kept, so a stock enquiry can still say what is physically there.
        Assert.Equal(10m, composed.Rows.Sum(row => row.OriginalQuantity));
    }

    [Fact]
    public void Commitments_come_off_the_batch_that_expires_first()
    {
        var composed = SnapshotComposer.Compose(
            batches:
            [
                Batch("CHE011", "LATE", quantity: 6, expiry: "2026-12-01"),
                Batch("CHE011", "SOON", quantity: 4, expiry: "2026-09-20")
            ],
            warehouseStock: [Stock("CHE011", inStock: 10, committed: 4)]);

        // The earliest-expiring stock is what an existing document will actually be given, so it is
        // not what a new sale can have.
        Assert.Equal(0m, composed.Rows.Single(row => row.BatchNumber == "SOON").AvailableQuantity);
        Assert.Equal(6m, composed.Rows.Single(row => row.BatchNumber == "LATE").AvailableQuantity);
    }

    [Fact]
    public void A_fully_committed_batch_keeps_its_row()
    {
        var composed = SnapshotComposer.Compose(
            batches: [Batch("CHE011", "B-1", quantity: 5, expiry: "2026-10-01")],
            warehouseStock: [Stock("CHE011", inStock: 5, committed: 5)]);

        // Written, not dropped: "this batch is here and holds nothing sellable" is what a stock
        // enquiry should show, and a missing row reads as a missing batch.
        var row = Assert.Single(composed.Rows);
        Assert.Equal(0m, row.AvailableQuantity);
        Assert.Equal(5m, row.OriginalQuantity);
    }

    // ---------------------------------------------------------------
    // Figures that do not add up
    // ---------------------------------------------------------------

    [Fact]
    public void Batches_bind_when_the_warehouse_read_claims_more_than_they_hold()
    {
        var composed = SnapshotComposer.Compose(
            batches: [Batch("CHE011", "B-1", quantity: 3, expiry: "2026-10-01")],
            warehouseStock: [Stock("CHE011", inStock: 9, committed: 0)]);

        // SAP will only release what the batches hold, so promising nine would be promising six
        // units that cannot be allocated.
        Assert.Equal(3m, composed.Rows.Sum(row => row.AvailableQuantity));
        Assert.Contains(composed.Notes, note => note.Contains("batches hold only"));
    }

    [Fact]
    public void Batches_for_an_item_the_warehouse_read_omits_are_reported_not_guessed()
    {
        var composed = SnapshotComposer.Compose(
            batches: [Batch("GHOST01", "B-1", quantity: 7, expiry: "2026-10-01")],
            warehouseStock: []);

        // With no issuable figure there is nothing to say how much may be promised, and guessing the
        // batch total is exactly the gross-quantity mistake this pass removed.
        Assert.Empty(composed.Rows);
        Assert.Contains(composed.Notes, note => note.Contains("GHOST01") && note.Contains("cannot be sold"));
    }

    [Fact]
    public void A_warehouse_with_nothing_in_it_composes_to_nothing()
    {
        var composed = SnapshotComposer.Compose(batches: [], warehouseStock: []);

        Assert.Empty(composed.Rows);
        Assert.Empty(composed.Notes);
    }

    // ---------------------------------------------------------------

    private static StockQuantityDto Stock(string itemCode, decimal inStock, decimal committed) => new()
    {
        ItemCode = itemCode,
        ItemName = itemCode,
        InStock = inStock,
        Committed = committed
    };

    private static BatchNumber Batch(string itemCode, string batchNum, decimal quantity, string expiry) => new()
    {
        ItemCode = itemCode,
        ItemName = itemCode,
        BatchNum = batchNum,
        Quantity = quantity,
        ExpiryDate = expiry
    };
}
