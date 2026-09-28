using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sap;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Tests;

/// <summary>
/// Pins how far back a posting pass reaches once an outage has held sales up.
/// </summary>
public sealed class SapOutageReachTests : IDisposable
{
    // 10:00 CAT on the 28th.
    private static readonly DateTime Now = new(2026, 9, 28, 8, 0, 0, DateTimeKind.Utc);

    // The till pass's own cutoff: three days back, as a UTC-kinded date.
    private static readonly DateTime TillCutoff = new(2026, 9, 25, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public SapOutageReachTests()
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
    public async Task With_no_outage_the_pass_keeps_its_own_window()
    {
        Assert.Equal(TillCutoff, await ReachAsync(TillCutoff));
    }

    [Fact]
    public async Task An_outage_over_before_the_window_opened_does_not_widen_it()
    {
        await GivenOutageAsync(started: new(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc), ended: new(2026, 9, 21, 8, 0, 0, DateTimeKind.Utc));

        Assert.Equal(TillCutoff, await ReachAsync(TillCutoff));
    }

    [Fact]
    public async Task A_four_day_outage_that_ended_this_morning_takes_the_window_back_to_the_day_before_it_began()
    {
        // Started 09:00 CAT on the 23rd, over at 07:00 CAT today. A sale made on the 23rd is five days
        // old: outside the three-day till window, and owed all the same.
        await GivenOutageAsync(started: new(2026, 9, 23, 7, 0, 0, DateTimeKind.Utc), ended: new(2026, 9, 28, 5, 0, 0, DateTimeKind.Utc));

        var reach = await ReachAsync(TillCutoff);

        Assert.Equal(new DateTime(2026, 9, 22), reach.Date);
        Assert.Equal(DateTimeKind.Utc, reach.Kind);
    }

    [Fact]
    public async Task An_outage_still_going_on_widens_the_window()
    {
        await GivenOutageAsync(started: new(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc), ended: null);

        Assert.Equal(new DateTime(2026, 9, 20), (await ReachAsync(TillCutoff)).Date);
    }

    [Fact]
    public async Task An_outage_that_began_in_the_evening_is_dated_by_the_trading_day_in_cat()
    {
        // 23:30 UTC on the 21st is 01:30 CAT on the 22nd: the trading day is the 22nd, and the day
        // before it is the 21st.
        await GivenOutageAsync(started: new(2026, 9, 21, 23, 30, 0, DateTimeKind.Utc), ended: null);

        Assert.Equal(new DateTime(2026, 9, 21), (await ReachAsync(TillCutoff)).Date);
    }

    [Fact]
    public async Task An_outage_left_open_for_months_cannot_drag_the_window_further_than_the_bound()
    {
        await GivenOutageAsync(started: new(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc), ended: null);

        Assert.Equal(new DateTime(2026, 8, 29), (await ReachAsync(TillCutoff, maxExtensionDays: 30)).Date);
    }

    [Fact]
    public async Task A_window_already_wider_than_the_outage_is_left_alone()
    {
        await GivenOutageAsync(started: new(2026, 9, 26, 8, 0, 0, DateTimeKind.Utc), ended: null);

        var vanCutoff = new DateTime(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(vanCutoff, await ReachAsync(vanCutoff));
    }

    private Task<DateTime> ReachAsync(DateTime cutoff, int maxExtensionDays = 30) =>
        SapOutageReach.ExtendAsync(_context, cutoff, maxExtensionDays, Now, CancellationToken.None);

    private async Task GivenOutageAsync(DateTime started, DateTime? ended)
    {
        _context.SapOutages.Add(new SapOutageEntity
        {
            StartedAtUtc = started,
            DeclaredAtUtc = started.AddMinutes(1),
            EndedAtUtc = ended,
            Cause = SapOutageCauses.Unreachable,
            LastProbeAtUtc = ended ?? Now
        });
        await _context.SaveChangesAsync();
    }
}
