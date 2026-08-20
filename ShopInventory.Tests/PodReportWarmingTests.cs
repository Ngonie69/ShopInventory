using ShopInventory.Common.Caching;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Covers which POD reports get rebuilt off the request path, and when.
/// </summary>
/// <remarks>
/// A cache hit answers in 30–90 ms; on 2026-08-20 six misses landed in ninety minutes at 6 to 64
/// seconds each, every one paid for by somebody waiting. Snapshots live fifteen minutes and users
/// pick their own date ranges, so ordinary morning use straddles expiry repeatedly.
/// <para>
/// The trap in warming is doing too much of it: a fixed list of presets on a timer is several
/// hundred SAP rebuilds a day for reports nobody opened. These tests hold the property that makes it
/// pay — warming follows what was actually asked for, and stops when nobody is asking.
/// </para>
/// </remarks>
public sealed class PodReportWarmingTests
{
    private static readonly TimeSpan Freshness = TimeSpan.FromMinutes(15);
    private static readonly DateTime Now = new(2026, 8, 20, 9, 0, 0, DateTimeKind.Utc);

    private static PodReportWarmKey Shape(int days) =>
        new(new DateTime(2026, 8, 20).AddDays(-days), new DateTime(2026, 8, 20), "global");

    [Fact]
    public void Nothing_is_warmed_until_somebody_asks_for_something()
    {
        Assert.Empty(new PodReportWarmSet().ActiveShapes());
    }

    [Fact]
    public void A_requested_shape_becomes_a_warming_candidate()
    {
        var set = new PodReportWarmSet();
        set.Record(Shape(30));

        Assert.Equal([Shape(30)], set.ActiveShapes());
    }

    [Fact]
    public void Asking_for_the_same_shape_twice_tracks_it_once()
    {
        var set = new PodReportWarmSet();
        set.Record(Shape(30));
        set.Record(Shape(30));

        Assert.Single(set.ActiveShapes());
    }

    /// <summary>
    /// Two users on different ranges are two shapes; one user's range must not warm on the other's
    /// key or the snapshot saved would answer the wrong question.
    /// </summary>
    [Fact]
    public void Different_ranges_are_different_shapes()
    {
        var set = new PodReportWarmSet();
        set.Record(Shape(7));
        set.Record(Shape(30));

        Assert.Equal(2, set.ActiveShapes().Count);
    }

    [Fact]
    public void A_driver_scope_is_not_the_global_scope()
    {
        var set = new PodReportWarmSet();
        set.Record(new PodReportWarmKey(new DateTime(2026, 8, 1), new DateTime(2026, 8, 20), "global"));
        set.Record(new PodReportWarmKey(new DateTime(2026, 8, 1), new DateTime(2026, 8, 20), "driver-ABC"));

        Assert.Equal(2, set.ActiveShapes().Count);
    }

    /// <summary>
    /// A run of one-off custom ranges must not turn the warm job into a rebuild storm. The coldest
    /// shapes are dropped, so what survives is what somebody keeps coming back to.
    /// </summary>
    [Fact]
    public void The_tracked_set_is_bounded()
    {
        var set = new PodReportWarmSet();

        for (var days = 1; days <= 40; days++)
        {
            set.Record(Shape(days));
        }

        var active = set.ActiveShapes();
        Assert.True(active.Count <= 12, $"Expected the set to stay bounded; it held {active.Count}.");

        // The most recent request survives the trim.
        Assert.Contains(Shape(40), active);
    }

    [Fact]
    public void A_snapshot_well_inside_its_freshness_window_is_left_alone()
    {
        var snapshot = Snapshot(refreshedAt: Now.AddMinutes(-1), isFresh: true, creditNoteDataComplete: true);

        Assert.True(PodReportWarmJob.IsWarmEnough(snapshot, Freshness, Now));
    }

    [Fact]
    public void A_snapshot_about_to_lapse_is_rebuilt_before_anyone_hits_it()
    {
        // 12 minutes into a 15-minute window: 3 minutes left, inside the 5-minute refresh-ahead.
        var snapshot = Snapshot(refreshedAt: Now.AddMinutes(-12), isFresh: true, creditNoteDataComplete: true);

        Assert.False(PodReportWarmJob.IsWarmEnough(snapshot, Freshness, Now));
    }

    [Fact]
    public void A_missing_snapshot_is_rebuilt()
    {
        Assert.False(PodReportWarmJob.IsWarmEnough(null, Freshness, Now));
    }

    /// <summary>
    /// The reader rebuilds a snapshot with incomplete credit-note data however fresh it is, so
    /// warming has to agree — otherwise the job would skip exactly the snapshots that always miss.
    /// </summary>
    [Fact]
    public void A_fresh_snapshot_with_incomplete_credit_note_data_is_still_rebuilt()
    {
        var snapshot = Snapshot(refreshedAt: Now.AddMinutes(-1), isFresh: true, creditNoteDataComplete: false);

        Assert.False(PodReportWarmJob.IsWarmEnough(snapshot, Freshness, Now));
    }

    private static PodReportCacheSnapshot Snapshot(
        DateTime refreshedAt,
        bool isFresh,
        bool creditNoteDataComplete) =>
        new(new PodUploadStatusReportDto { CreditNoteDataComplete = creditNoteDataComplete },
            refreshedAt,
            isFresh);
}
