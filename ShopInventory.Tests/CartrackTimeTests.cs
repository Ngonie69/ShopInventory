using ShopInventory.Services;
using ShopInventory.Services.Telematics;
using Xunit;

namespace ShopInventory.Tests;

/// <summary>
/// Turning a Cartrack timestamp into an instant, and an instant into a trading date.
/// </summary>
/// <remarks>
/// Asserted through <see cref="AuditService.ToCAT"/> and <see cref="AuditService.FromCAT"/>
/// rather than against a hardcoded +2. A test that assumed the offset would agree with a broken
/// implementation that assumed the same thing, and the whole point of routing this through the
/// app's existing converter is that telematics cannot disagree with the rest of the report about
/// what a day is.
/// </remarks>
public class CartrackTimeTests
{
    [Fact]
    public void A_timestamp_with_a_two_digit_offset_is_read_as_that_offset()
    {
        // The wire format: a space instead of a T, and +02 rather than +02:00.
        var utc = CartrackTime.ToUtc("2026-09-18 06:52:03+02");

        Assert.Equal(new DateTime(2026, 9, 18, 4, 52, 3, DateTimeKind.Utc), utc);
    }

    [Fact]
    public void A_timestamp_with_a_full_offset_is_read_as_that_offset()
    {
        Assert.Equal(
            new DateTime(2026, 9, 18, 4, 52, 3, DateTimeKind.Utc),
            CartrackTime.ToUtc("2026-09-18 06:52:03+02:00"));
    }

    [Fact]
    public void An_offset_is_believed_rather_than_assumed()
    {
        // The guard against a local-kind parse: a reading that arrives in a different offset has
        // to land somewhere different, not two hours from wherever the server happens to be.
        Assert.Equal(
            new DateTime(2026, 9, 18, 6, 52, 3, DateTimeKind.Utc),
            CartrackTime.ToUtc("2026-09-18 06:52:03+00:00"));
    }

    [Fact]
    public void A_timestamp_with_no_offset_is_read_as_the_accounts_own_clock()
    {
        Assert.Equal(
            AuditService.FromCAT(new DateTime(2026, 9, 18, 6, 52, 3)),
            CartrackTime.ToUtc("2026-09-18 06:52:03"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a timestamp")]
    public void An_absent_or_unreadable_timestamp_is_null(string? value)
    {
        Assert.Null(CartrackTime.ToUtc(value));
    }

    // — Trading dates ——————————————————————————————————————————————————

    [Fact]
    public void An_ignition_just_after_midnight_belongs_to_that_day()
    {
        var utc = CartrackTime.ToUtc("2026-09-18 00:30:00+02")!.Value;

        Assert.Equal(new DateTime(2026, 9, 18), CartrackTime.TradingDateOf(utc));
    }

    [Fact]
    public void An_ignition_just_before_midnight_belongs_to_the_day_it_happened_not_the_next()
    {
        // 23:30 CAT is 21:30 UTC the same date, so reading .Date off the instant happens to be
        // right here — which is exactly why the next case matters.
        var utc = CartrackTime.ToUtc("2026-09-18 23:30:00+02")!.Value;

        Assert.Equal(new DateTime(2026, 9, 18), CartrackTime.TradingDateOf(utc));
    }

    [Fact]
    public void An_instant_after_ten_at_night_utc_belongs_to_the_next_trading_day()
    {
        // 22:30 UTC is 00:30 CAT on the 19th. Reading .Date off the instant would file this
        // under the 18th and attach a van's first movement to the wrong rep-day.
        var utc = new DateTime(2026, 9, 18, 22, 30, 0, DateTimeKind.Utc);

        Assert.Equal(new DateTime(2026, 9, 19), CartrackTime.TradingDateOf(utc));
    }

    [Fact]
    public void A_trading_dates_window_is_exactly_the_api_call_cap()
    {
        // The events and temperature endpoints allow 24 hours per call, and one CAT day is
        // exactly 24 hours. If that ever stopped being true those reads would start failing.
        var (from, to) = CartrackTime.UtcWindowOf(new DateTime(2026, 9, 18));

        Assert.Equal(TimeSpan.FromHours(24), to - from);
    }

    [Fact]
    public void A_trading_dates_window_starts_at_midnight_on_the_accounts_clock()
    {
        var (from, to) = CartrackTime.UtcWindowOf(new DateTime(2026, 9, 18));

        Assert.Equal(AuditService.FromCAT(new DateTime(2026, 9, 18)), from);
        Assert.Equal(AuditService.FromCAT(new DateTime(2026, 9, 19)), to);
    }

    [Fact]
    public void Every_instant_in_a_window_belongs_to_that_trading_date()
    {
        var date = new DateTime(2026, 9, 18);
        var (from, to) = CartrackTime.UtcWindowOf(date);

        Assert.Equal(date, CartrackTime.TradingDateOf(from));
        Assert.Equal(date, CartrackTime.TradingDateOf(to.AddTicks(-1)));

        // Half-open: the upper bound is the next day's first instant, not this day's last.
        Assert.Equal(date.AddDays(1), CartrackTime.TradingDateOf(to));
    }

    // — Request formatting ————————————————————————————————————————————

    [Fact]
    public void A_request_timestamp_carries_no_offset()
    {
        // Cartrack read an offsetless parameter in the account's local time, so sending one with
        // an offset on it is not a safer version of the same thing.
        var formatted = CartrackTime.ToRequestString(
            AuditService.FromCAT(new DateTime(2026, 9, 18, 6, 52, 3)));

        Assert.Equal("2026-09-18 06:52:03", formatted);
    }

    [Fact]
    public void A_date_parameter_is_the_bare_calendar_date()
    {
        Assert.Equal("2026-09-18", CartrackTime.ToDateString(new DateTime(2026, 9, 18, 13, 5, 0)));
    }

    [Fact]
    public void A_round_trip_through_the_wire_format_returns_the_same_instant()
    {
        var original = AuditService.FromCAT(new DateTime(2026, 9, 18, 6, 52, 3));

        Assert.Equal(original, CartrackTime.ToUtc(CartrackTime.ToRequestString(original)));
    }
}
