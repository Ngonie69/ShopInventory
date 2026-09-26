using System.Text.Json;
using ShopInventory.Features.VanSalesReports.Queries.GetVanReplenishmentReport;
using ShopInventory.Features.VanSalesReports.Queries.GetVanStockReport;
using ShopInventory.Web.Models;

namespace ShopInventory.Tests;

/// <summary>
/// Sends the two stock reports across the wire and reads them back as the portal's hand-mirrored
/// DTOs.
/// </summary>
/// <remarks>
/// Same guard as the other van reports. The nulls in these two carry particular weight: a van that
/// asked for nothing has no service level rather than a perfect one, a van loaded with nothing has no
/// sell-through, and a morning SAP could not check is unchecked rather than tied. If any
/// of those is non-nullable on the portal side, System.Text.Json throws inside GetFromJsonAsync, the
/// service's catch turns it into a null, and the page renders "no data".
/// </remarks>
public class VanStockContractTests
{
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);

    private static T RoundTrip<TSource, T>(TSource result)
    {
        var mirrored = JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(result, Wire), Wire);
        Assert.NotNull(mirrored);
        return mirrored;
    }

    // ── Replenishment ───────────────────────────────────────────────────────────

    [Fact]
    public void A_populated_replenishment_report_crosses_the_wire_intact()
    {
        var mirrored = RoundTrip<VanReplenishmentReportResult, VanReplenishmentReportResponse>(
            PopulatedReplenishment());

        Assert.Equal(12, mirrored.Summary.RequestCount);
        Assert.Equal(10, mirrored.Summary.FillBase);
        Assert.Equal(0.8, mirrored.Summary.FilledWithinDayRate!.Value, 3);
        Assert.Equal(6d, mirrored.Summary.MedianHoursToDecision);
        Assert.Equal(20d, mirrored.Summary.SlowestTenthHoursToPosting);
        Assert.Equal(25, mirrored.Summary.OldestUnfilledDays);

        var band = Assert.Single(mirrored.Waits);
        Assert.Equal(ShopInventory.Web.Models.VanReplenishmentWaitBands.UnderOneHour, band.Band);
        Assert.Equal(7, band.Count);

        var van = Assert.Single(mirrored.Vans);
        Assert.Equal("VAN010", van.VanWarehouseCode);
        Assert.Equal(["KEFGRC"], van.DepotWarehouses);
        Assert.True(van.IsAssigned);
        Assert.Equal(3, van.DaysSinceLastPosted);
        Assert.True(van.LastPostedBeforePeriod);
        Assert.Equal(0.8, van.FilledWithinDayRate!.Value, 3);
        Assert.Equal(2, van.UnfilledCount);

        var stuck = Assert.Single(mirrored.Unfilled);
        Assert.Equal(ShopInventory.Web.Models.VanReplenishmentCauses.DepotShort, stuck.Cause);
        Assert.True(stuck.RaisedByDepot);
        Assert.Equal(25, stuck.DaysWaiting);
        Assert.Equal(20, stuck.LinesInStockAtLastAttempt);
        Assert.Equal(360m, Assert.Single(stuck.ShortItems).Shortage);

        Assert.Equal(["VAN010", "VAN011"], mirrored.AvailableVans);
        Assert.Equal(["KEFBYC", "KEFGRC"], mirrored.AvailableDepots);

        var depot = Assert.Single(mirrored.DepotShortages);
        Assert.Equal("KEFGRC", depot.DepotWarehouseCode);
        Assert.Equal(2, Assert.Single(depot.Items).RequestCount);
    }

    /// <summary>
    /// A van that asked for nothing has no service level and has never been supplied. Both are nulls
    /// meaning "cannot say", and both would be a lie as zeros.
    /// </summary>
    [Fact]
    public void A_quiet_van_survives_the_mirror_with_its_nulls_intact()
    {
        var mirrored = RoundTrip<VanReplenishmentReportResult, VanReplenishmentReportResponse>(
            new VanReplenishmentReportResult(
                new DateTime(2026, 8, 1),
                new DateTime(2026, 8, 31),
                new DateTime(2026, 9, 1, 8, 0, 0),
                new VanReplenishmentSummaryResult(1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null, null, null, null, 0, 0, null),
                [],
                [
                    new VanReplenishmentVanResult(
                        "VAN010", [], true, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0m, null, null, null, null, null, null, 0)
                ],
                [],
                [],
                new VanReplenishmentQualityResult(0, 0, 0, 1, 0),
                ["VAN010"],
                ["KEFGRC"]));

        var van = Assert.Single(mirrored.Vans);

        Assert.Null(van.FilledWithinDayRate);
        Assert.Null(van.MedianHoursToDecision);
        Assert.Null(van.MedianHoursToPosting);
        Assert.Null(van.SlowestHoursToPosting);
        Assert.Null(van.LastRequestedAt);
        Assert.Null(van.LastPostedAt);
        Assert.Null(van.DaysSinceLastPosted);

        Assert.Null(mirrored.Summary.FilledWithinDayRate);
        Assert.Null(mirrored.Summary.OldestUnfilledDays);
        Assert.Contains(mirrored.Quality.Caveats, caveat => caveat.Contains("no restock request"));
    }

    /// <summary>
    /// The drawer's live stock check. A line that fits carries no available figure — null, which the
    /// portal must not read as zero in stock.
    /// </summary>
    [Fact]
    public void A_stock_check_crosses_the_wire_with_its_unknown_figures_null()
    {
        var mirrored = RoundTrip<ShopInventory.Features.InventoryTransfers.Queries.GetPendingTransferStockCheck.PendingTransferStockCheckResult, PendingTransferStockCheck>(
            new(
                Guid.NewGuid(), "KEFBYC", "VAN010", new DateTime(2026, 9, 26, 10, 0, 0), true, [],
                [
                    new(1, "YOG100", "EA", null, 360m, 0m, "Short"),
                    new(2, "MLK201", "EA", null, 240m, null, "InStock")
                ]));

        Assert.Equal(1, mirrored.LinesShort);
        Assert.Equal(1, mirrored.LinesInStock);
        Assert.True(mirrored.Lines[0].IsShort);
        Assert.Equal(0m, mirrored.Lines[0].AvailableQuantity);
        Assert.Null(mirrored.Lines[1].AvailableQuantity);
    }

// ── Stock ───────────────────────────────────────────────────────────────────

    [Fact]
    public void A_populated_stock_report_crosses_the_wire_intact()
    {
        var mirrored = RoundTrip<VanStockReportResult, VanStockReportResponse>(PopulatedStock());

        Assert.Equal(14, mirrored.DeadStockDays);
        Assert.Equal(3, mirrored.Summary.SnapshotAgeDays);
        Assert.True(mirrored.Summary.IsStale);
        Assert.True(mirrored.Summary.SapChecked);
        Assert.Equal(new DateTime(2026, 8, 14, 7, 4, 0), mirrored.Summary.LatestCountAt);

        var day = Assert.Single(mirrored.Days);
        Assert.Equal(90m, day.ExpectedRemaining);
        Assert.False(day.SoldBeyondLoad);

        var morning = Assert.Single(mirrored.Mornings);
        Assert.False(morning.HasGap);
        Assert.False(morning.TiesToSap);
        Assert.Equal(1, morning.ItemsUnexplained);

        var document = Assert.Single(morning.Documents);
        Assert.Equal("Invoice", document.Kind);
        Assert.Equal(2, document.DaysBackdated);

        var unexplained = Assert.Single(morning.Unexplained);
        Assert.Equal(40m, unexplained.Expected);
        Assert.Equal(-9m, unexplained.Unexplained);

        var sales = Assert.Single(mirrored.SalesDays);
        Assert.Equal("Late", sales.Status);
        Assert.Equal(2, sales.DaysLate);
        Assert.Equal(-5m, Assert.Single(sales.Differences).Difference);

        var van = Assert.Single(mirrored.Vans);
        Assert.Equal(["VAN010"], van.AccountCodes);
        Assert.Equal(1, van.MorningsWithOtherPostings);
        Assert.Equal(2, van.MaxDaysLate);

        var item = Assert.Single(mirrored.Items);
        Assert.True(item.IsDead);

        var batch = Assert.Single(mirrored.Expiring);
        Assert.True(batch.HasExpired);

        // The page shows the API's own sentences rather than rewording them.
        Assert.False(mirrored.Quality.IsClean);
        Assert.Contains(mirrored.Quality.Caveats, caveat => caveat.Contains("day(s) old"));
    }

    /// <summary>
    /// When SAP could not be read, nothing is claimed either way: a morning is unchecked rather than
    /// tied, a day's sales are unchecked rather than on time, and the reason reaches the page.
    /// </summary>
    [Fact]
    public void A_report_without_sap_arrives_unchecked_rather_than_clean()
    {
        var mirrored = RoundTrip<VanStockReportResult, VanStockReportResponse>(
            new VanStockReportResult(
                new DateTime(2026, 8, 1),
                new DateTime(2026, 8, 31),
                14,
                new VanStockSummaryResult(1, 2, 1, 0, 0, 0m, 0m, null, null),
                [],
                [
                    new VanStockMorningResult(
                        "VAN010", new DateTime(2026, 8, 4), new DateTime(2026, 8, 6),
                        new DateTime(2026, 8, 4, 7, 2, 0), new DateTime(2026, 8, 6, 7, 3, 0),
                        GapDays: 2, SapChecked: false, 3, 0, 0, [], [])
                ],
                [
                    new VanStockSalesDayResult(
                        "VAN010", new DateTime(2026, 8, 4), "Unchecked", 2, 0, 0, null, null,
                        new DateTime(2026, 8, 5, 7, 0, 0), null, 0, 0, [])
                ],
                [],
                [],
                new VanStockQualityResult(1, 0, 0, 0, null, null,
                    SapProblem: "SAP documents could not be read: timeout.")));

        var morning = Assert.Single(mirrored.Mornings);

        Assert.True(morning.HasGap);
        Assert.False(morning.SapChecked);
        Assert.False(morning.TiesToSap);
        Assert.Equal("Unchecked", Assert.Single(mirrored.SalesDays).Status);
        Assert.Null(Assert.Single(mirrored.SalesDays).LastInvoicedAt);

        Assert.False(mirrored.Summary.SapChecked);
        Assert.Equal("SAP documents could not be read: timeout.", mirrored.Quality.SapProblem);
        Assert.Contains(mirrored.Quality.Caveats, caveat => caveat.Contains("could not be read"));
        Assert.Contains(mirrored.Quality.Caveats, caveat => caveat.Contains("across the gap"));
    }

    /// <summary>A van loaded with nothing has no sell-through, and an empty period no lists to loop.</summary>
    [Fact]
    public void An_empty_stock_report_arrives_with_empty_lists_and_null_rates()
    {
        var mirrored = RoundTrip<VanStockReportResult, VanStockReportResponse>(
            new VanStockReportResult(
                new DateTime(2026, 8, 1),
                new DateTime(2026, 8, 31),
                14,
                new VanStockSummaryResult(0, 0, 0, 0, 0, 0m, 0m, null, null, SapChecked: true),
                [], [], [], [], [],
                new VanStockQualityResult(0, 0, 0, 0, null, null)));

        Assert.Empty(mirrored.Days);
        Assert.Empty(mirrored.Mornings);
        Assert.Empty(mirrored.SalesDays);
        Assert.Empty(mirrored.Items);
        Assert.Empty(mirrored.Expiring);
        Assert.Null(mirrored.Summary.SellThroughRate);
        Assert.True(mirrored.Quality.IsClean);
        Assert.Empty(mirrored.Quality.Caveats);
    }

    // ── Fixtures ────────────────────────────────────────────────────────────────

    private static VanReplenishmentReportResult PopulatedReplenishment() => new(
        new DateTime(2026, 8, 1),
        new DateTime(2026, 8, 31),
        new DateTime(2026, 9, 1, 8, 0, 0),
        new VanReplenishmentSummaryResult(
            VanCount: 1,
            VansAsking: 1,
            RequestCount: 12,
            PostedCount: 9,
            PartlyPostedCount: 1,
            RejectedCount: 1,
            CancelledCount: 1,
            WithdrawnAfterFailureCount: 0,
            OpenCount: 1,
            LineCount: 96,
            FilledWithinDayCount: 8,
            MedianHoursToDecision: 6d,
            SlowestTenthHoursToDecision: 9d,
            MedianHoursToPosting: 11d,
            SlowestTenthHoursToPosting: 20d,
            UnfilledNowCount: 1,
            VansWaitingNow: 1,
            OldestUnfilledDays: 25),
        [new VanReplenishmentWaitBandResult("UnderOneHour", 7)],
        [
            new VanReplenishmentVanResult(
                "VAN010", ["KEFGRC"], true, 12, 9, 1, 1, 1, 1, 1, 8, 96, 1440m,
                6d, 11d, 52d,
                new DateTime(2026, 8, 28, 7, 0, 0),
                new DateTime(2026, 7, 28, 18, 0, 0),
                3,
                1)
            {
                LastPostedBeforePeriod = true
            }
        ],
        [
            new VanReplenishmentOpenRequestResult(
                Guid.NewGuid(), "DT-2026-00412", "VAN010", "KEFGRC", "PostFailed", "DepotShort",
                "Bulawayo Controller", "DepotController", true,
                new DateTime(2026, 8, 1, 16, 55, 0), new DateTime(2026, 8, 1, 17, 2, 0), new DateTime(2026, 8, 1, 17, 2, 0),
                25, 1200m, 5, false,
                [new VanReplenishmentShortItemResult("YOG100", 360m)],
                "Insufficient stock in source warehouse: ...", 600.5)
        ],
        [
            new VanReplenishmentDepotShortageResult(
                "KEFGRC", 2, 1, 42, 35, false,
                [new VanReplenishmentDepotShortItemResult("YOG100", 600m, 2)])
        ],
        new VanReplenishmentQualityResult(0, 0, 0, 0, 0),
        ["VAN010", "VAN011"],
        ["KEFBYC", "KEFGRC"]);

private static VanStockReportResult PopulatedStock() => new(
        new DateTime(2026, 8, 1),
        new DateTime(2026, 8, 31),
        14,
        new VanStockSummaryResult(
            VanCount: 1,
            SnapshotDayCount: 2,
            MissingSnapshotDays: 0,
            ItemCount: 1,
            DeadItemCount: 1,
            LoadedQuantity: 150m,
            SoldQuantity: 60m,
            LatestSnapshotDate: new DateTime(2026, 8, 14),
            SnapshotAgeDays: 3,
            SapChecked: true,
            LatestCountAt: new DateTime(2026, 8, 14, 7, 4, 0)),
        [
            new VanStockDayResult("VAN010", new DateTime(2026, 8, 4), true, 2, 150m, 60m, 0m, 1, 1)
        ],
        [
            new VanStockMorningResult(
                "VAN010", new DateTime(2026, 8, 4), new DateTime(2026, 8, 5),
                new DateTime(2026, 8, 4, 7, 3, 0), new DateTime(2026, 8, 5, 7, 2, 0),
                GapDays: 1, SapChecked: true, ItemCount: 2, ItemsMoved: 1, ItemsUnexplained: 1,
                [
                    new VanStockDocumentResult(
                        "Invoice", 2389341, 779350, new DateTime(2026, 8, 2),
                        new DateTime(2026, 8, 4, 9, 22, 0), true, 1, null)
                ],
                [new VanStockItemMovementResult("CHE011", "Cheddar 1kg", 100m, 60m, 0m, 0m, 0m, 31m)])
        ],
        [
            new VanStockSalesDayResult(
                "VAN010", new DateTime(2026, 8, 2), "Late", 1, 1, 1,
                new DateTime(2026, 8, 4, 9, 22, 0), new DateTime(2026, 8, 4, 9, 22, 0),
                new DateTime(2026, 8, 3, 7, 1, 0), 2, 1, 0,
                [new VanStockSalesItemResult("CHE011", "Cheddar 1kg", 65m, 60m)])
        ],
        [
            new VanStockItemResult("PIC003", "Pickles 500g", 1, 20, 0, 20, 400m, 0m, null)
        ],
        [
            new VanStockExpiryResult(
                "VAN010", "CHE011", "Cheddar 1kg", "BATCH-A",
                new DateTime(2026, 8, 10), -7, 12m, new DateTime(2026, 8, 14))
        ],
        new VanStockQualityResult(0, 0, 0, 0, new DateTime(2026, 8, 14), 3),
        [
            new VanStockVanResult(
                "VAN010", 2, 1, 2, 4, 1, 1, ["VAN010"],
                MorningsChecked: 1, MorningsTied: 0, SalesDays: 1, SalesDaysLate: 1, MaxDaysLate: 2)
        ]);
}
