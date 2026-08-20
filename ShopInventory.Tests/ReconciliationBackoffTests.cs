using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Pins the cadence at which reconciliation re-asks SAP about an order it has already given up on.
/// </summary>
/// <remarks>
/// The sweep runs every two minutes over a seven-day lookback, so an order SAP never received is
/// re-probed 720 times a day forever. On 2026-08-20 that was 276 sweeps in nine hours, every one of
/// them naming SO-20260817-0008 — unlinked since the 17th, roughly two thousand futile probes, all
/// at Information where nobody would see them.
/// <para>
/// The recent window is where a repair actually happens: a SAP create that committed shows up within
/// moments. Everything older is swept on the half hour instead.
/// </para>
/// </remarks>
public sealed class ReconciliationBackoffTests
{
    private static readonly TimeSpan TriggerInterval = TimeSpan.FromMinutes(2);

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("00:30:00")]
    [InlineData("08:00:00")]
    [InlineData("08:30:00")]
    [InlineData("23:30:00")]
    public void The_full_lookback_is_swept_on_the_half_hour(string timeOfDay)
    {
        Assert.True(IsFullSweepAt(timeOfDay));
    }

    [Theory]
    [InlineData("00:02:00")]
    [InlineData("00:16:00")]
    [InlineData("00:28:00")]
    [InlineData("08:34:00")]
    [InlineData("08:58:00")]
    public void Every_other_run_sweeps_only_the_recent_window(string timeOfDay)
    {
        Assert.False(IsFullSweepAt(timeOfDay));
    }

    /// <summary>
    /// A trigger that fires a little late still counts as the full sweep, or a run that drifts by
    /// seconds would skip the half hour entirely and leave a stuck order unreported for another one.
    /// </summary>
    [Fact]
    public void A_run_that_fires_slightly_late_still_counts_as_the_full_sweep()
    {
        Assert.True(IsFullSweepAt("08:30:41"));
        Assert.True(IsFullSweepAt("08:31:59"));
    }

    /// <summary>
    /// Exactly one run in each half hour is a full sweep — not two, which would double the SAP
    /// probes, and not zero, which would strand the aged orders.
    /// </summary>
    [Fact]
    public void Exactly_one_run_per_half_hour_is_a_full_sweep()
    {
        var start = new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero);

        var fullSweeps = Enumerable.Range(0, 15)                    // one half hour at 2-minute steps
            .Select(step => start.AddMinutes(2 * step))
            .Count(fireTime => SalesOrderReconciliationJob.IsFullSweep(fireTime, TriggerInterval));

        Assert.Equal(1, fullSweeps);
    }

    [Fact]
    public void A_whole_day_of_runs_yields_forty_eight_full_sweeps()
    {
        var start = new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero);

        var fullSweeps = Enumerable.Range(0, 720)                   // 24h at 2-minute steps
            .Select(step => start.AddMinutes(2 * step))
            .Count(fireTime => SalesOrderReconciliationJob.IsFullSweep(fireTime, TriggerInterval));

        Assert.Equal(48, fullSweeps);
    }

    private static bool IsFullSweepAt(string timeOfDay) =>
        SalesOrderReconciliationJob.IsFullSweep(
            new DateTimeOffset(new DateTime(2026, 8, 20).Add(TimeSpan.Parse(timeOfDay)), TimeSpan.Zero),
            TriggerInterval);
}
