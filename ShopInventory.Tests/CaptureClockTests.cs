using ShopInventory.Common.Mobile;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// Pins how far a handset's clock is believed.
///
/// A van works out of coverage, so an attendance event or a day's departure is routinely recorded
/// hours before the server hears about it. Every one of these paths used to stamp
/// <c>DateTime.UtcNow</c> on arrival, which recorded the moment the signal came back rather than the
/// moment the rep was at the shop — silently, because nothing in the data said the time was the
/// sync's. The compliance report is built on those times, so the bounds below are load-bearing.
/// </summary>
public sealed class CaptureClockTests
{
    /// <summary>A fixed "now" so the tests do not drift with the wall clock.</summary>
    private static readonly DateTime Now = new(2026, 8, 12, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Resolve_falls_back_to_now_when_the_client_said_nothing()
    {
        // An older handset sends no captured_at at all. It must still be able to check in.
        Assert.Equal(Now, CaptureClock.Resolve(null, Now));
    }

    [Fact]
    public void Resolve_reads_a_cat_wall_clock_as_cat()
    {
        // 09:04 CAT is 07:04 UTC. Treating the value as UTC — the easy mistake, because it carries
        // no offset — would file every van's morning two hours early.
        var claimed = new DateTime(2026, 8, 12, 9, 4, 11);

        var resolved = CaptureClock.Resolve(claimed, Now);

        Assert.Equal(new DateTime(2026, 8, 12, 7, 4, 11, DateTimeKind.Utc), resolved);
        Assert.Equal(claimed, AuditService.ToCAT(resolved));
    }

    [Fact]
    public void Resolve_keeps_a_time_from_hours_earlier_in_the_day()
    {
        // The whole point: a 07:00 CAT departure synced at noon stays at 07:00.
        var departedCat = new DateTime(2026, 8, 12, 7, 0, 0);

        var resolved = CaptureClock.Resolve(departedCat, Now);

        Assert.Equal(7, AuditService.ToCAT(resolved).Hour);
    }

    [Fact]
    public void Resolve_tolerates_small_clock_skew_ahead_of_the_server()
    {
        // A handset a minute fast is ordinary drift, not a claim about the future.
        var slightlyAhead = AuditService.ToCAT(Now).AddMinutes(1);

        var resolved = CaptureClock.Resolve(slightlyAhead, Now);

        Assert.Equal(AuditService.FromCAT(slightlyAhead), resolved);
    }

    [Fact]
    public void Resolve_rejects_a_time_well_into_the_future()
    {
        var tomorrow = AuditService.ToCAT(Now).AddDays(1);

        Assert.Equal(Now, CaptureClock.Resolve(tomorrow, Now));
    }

    [Fact]
    public void Resolve_rejects_an_uninitialised_column_reading_back_as_the_year_one()
    {
        // sqlite-net migrates by adding columns, so a record queued before captured_at existed reads
        // back as DateTime default. Storing that would put a visit in the year 1.
        Assert.Equal(Now, CaptureClock.Resolve(default(DateTime), Now));
    }

    [Fact]
    public void Resolve_accepts_a_handset_that_has_been_offline_for_a_week()
    {
        var lastWeek = AuditService.ToCAT(Now).AddDays(-7);

        Assert.Equal(AuditService.FromCAT(lastWeek), CaptureClock.Resolve(lastWeek, Now));
    }

    [Theory]
    [InlineData("2026-08-12T09:04:11")]
    [InlineData("2026-08-12 09:04:11")]
    public void Parse_reads_both_shapes_the_handsets_send(string capturedAt)
    {
        Assert.Equal(new DateTime(2026, 8, 12, 9, 4, 11), CaptureClock.Parse(capturedAt));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a date")]
    public void Parse_returns_null_rather_than_guessing(string? capturedAt)
    {
        // Null falls through to the server clock. A guess would land the visit on some other day.
        Assert.Null(CaptureClock.Parse(capturedAt));
    }
}
