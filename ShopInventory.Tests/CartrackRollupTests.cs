using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Models.Telematics;
using ShopInventory.Services;
using ShopInventory.Services.Telematics;
using Xunit;

namespace ShopInventory.Tests;

/// <summary>
/// Turning the provider's raw events into one row per vehicle per trading day.
/// </summary>
/// <remarks>
/// The facts that matter most here are the ones about telling states apart. A van that sat still,
/// a van whose tracker is dead, and a read that failed all produce no figures, and the report has
/// to say something different about each — so several of these check the provenance flags rather
/// than the numbers.
/// </remarks>
public class CartrackRollupTests : IDisposable
{
    private const string Plate = "AFQ9644";
    private static readonly DateTime Day = new(2026, 9, 19);
    private static readonly Guid Rep = Guid.Parse("11111111-1111-1111-1111-111111111111");

    // The depot, and a customer about 4 km away — well outside any sane depot radius.
    private const double DepotLat = -17.8252;
    private const double DepotLon = 31.0335;
    private const double AwayLat = -17.7900;
    private const double AwayLon = 31.0335;

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public CartrackRollupTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);

        _context.Database.EnsureCreated();

        _context.Routes.Add(new RouteEntity
        {
            Code = "CBDCZA",
            Name = "CBD/CZA Truck",
            Territory = "Harare",
            TruckRegNo = Plate,
            IsActive = true
        });

        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>The read side, which is what the report holds. Same settings as the writer.</summary>
    private CartrackReadService Reader(Action<CartrackSettings>? tune = null)
    {
        var settings = Settings();
        tune?.Invoke(settings);

        return new CartrackReadService(_context, Options.Create(settings));
    }

    private static CartrackSettings Settings() => new()
    {
        Enabled = true,
        Username = "u",
        Password = "p",
        BackfillDays = 3,
        MaxBackfillDaysPerRun = 2,
        ReconciliationWindowDays = 3,
        DepotRadiusMetres = 250,
        RollupReadyMaxAgeHours = 36
    };

    private CartrackRollupService Service(StubClient client, Action<CartrackSettings>? tune = null)
    {
        var settings = Settings();
        tune?.Invoke(settings);

        return new CartrackRollupService(
            _context, client, client.Budget, Options.Create(settings),
            NullLogger<CartrackRollupService>.Instance);
    }

    private static DateTime At(int hour, int minute) =>
        AuditService.FromCAT(new DateTime(2026, 9, 19, hour, minute, 0));

    private static CartrackVehicleEvent Event(
        string description, int hour, int minute, double? lat = null, double? lon = null) => new()
        {
            Registration = Plate,
            EventDescription = description,
            EventTs = AuditService.ToCAT(At(hour, minute)).ToString("yyyy-MM-dd HH:mm:ss") + "+02",
            Latitude = lat,
            Longitude = lon
        };

    private void AddRouteDay(double? lat = DepotLat, double? lon = DepotLon)
    {
        _context.Users.Add(new User
        {
            Id = Rep,
            Username = "van010",
            Email = "van010@example.com",
            PasswordHash = "x",
            Role = "Sales",
            IsActive = true
        });

        _context.VanRouteDays.Add(new VanRouteDayEntity
        {
            UserId = Rep,
            Username = "van010",
            TradingDate = Day,
            TruckRegNo = Plate,
            DepartedAt = At(6, 40),
            DepartedLatitude = lat,
            DepartedLongitude = lon,
            PlannedCustomerCount = 10
        });

        _context.SaveChanges();
    }

    // — Departure ————————————————————————————————————————————————————

    [Fact]
    public async Task Ignition_and_departure_are_recorded_separately()
    {
        // The whole reason both exist: the key turns at 06:20 in the yard and the van does not
        // actually leave until 07:05. Reading the first ignition as the departure would call a
        // late round an early one.
        AddRouteDay();

        var client = new StubClient
        {
            Events =
            [
                Event("IGNITION_ON", 6, 20, DepotLat, DepotLon),
                Event("POSITION", 6, 45, DepotLat, DepotLon),
                Event("POSITION", 7, 5, AwayLat, AwayLon),
                Event("IGNITION_OFF", 16, 10, AwayLat, AwayLon)
            ]
        };

        await Service(client).BuildDayAsync(Plate, Day, CancellationToken.None);

        var row = Assert.Single(_context.VehicleDayRollups);

        Assert.Equal(At(6, 20), row.FirstIgnitionOnUtc);
        Assert.Equal(At(7, 5), row.FirstDepartureUtc);
        Assert.Equal(At(16, 10), row.LastIgnitionOffUtc);
        Assert.Equal(AwayLat, row.FirstDepartureLatitude);
    }

    [Fact]
    public async Task A_van_that_never_left_the_yard_has_an_ignition_but_no_departure()
    {
        // Null departure is the right answer and a different finding from leaving late.
        AddRouteDay();

        var client = new StubClient
        {
            Events =
            [
                Event("IGNITION_ON", 6, 20, DepotLat, DepotLon),
                Event("POSITION", 9, 0, DepotLat, DepotLon),
                Event("IGNITION_OFF", 9, 30, DepotLat, DepotLon)
            ]
        };

        await Service(client).BuildDayAsync(Plate, Day, CancellationToken.None);

        var row = Assert.Single(_context.VehicleDayRollups);

        Assert.NotNull(row.FirstIgnitionOnUtc);
        Assert.Null(row.FirstDepartureUtc);
        Assert.True(row.HasActivity);
    }

    [Fact]
    public async Task Departure_is_measured_from_where_the_rep_recorded_it()
    {
        // With no recorded point the first event is the origin, so a van that starts the day
        // already away from the depot would otherwise look as though it never left.
        AddRouteDay(lat: AwayLat, lon: AwayLon);

        var client = new StubClient
        {
            Events =
            [
                Event("IGNITION_ON", 6, 20, AwayLat, AwayLon),
                Event("POSITION", 7, 5, DepotLat, DepotLon)
            ]
        };

        await Service(client).BuildDayAsync(Plate, Day, CancellationToken.None);

        Assert.Equal(At(7, 5), _context.VehicleDayRollups.Single().FirstDepartureUtc);
    }

    [Fact]
    public async Task A_move_inside_the_depot_radius_is_not_a_departure()
    {
        AddRouteDay();

        var client = new StubClient
        {
            Events =
            [
                // Roughly 100 m north — shunting across the yard.
                Event("IGNITION_ON", 6, 20, DepotLat, DepotLon),
                Event("POSITION", 6, 30, DepotLat + 0.0009, DepotLon)
            ]
        };

        await Service(client).BuildDayAsync(Plate, Day, CancellationToken.None);

        Assert.Null(_context.VehicleDayRollups.Single().FirstDepartureUtc);
    }

    [Fact]
    public async Task The_ignition_cycle_count_is_recorded()
    {
        AddRouteDay();

        var client = new StubClient
        {
            Events =
            [
                Event("IGNITION_ON", 6, 20, DepotLat, DepotLon),
                Event("IGNITION_OFF", 9, 0, AwayLat, AwayLon),
                Event("IGNITION_ON", 9, 30, AwayLat, AwayLon),
                Event("IGNITION_OFF", 16, 0, AwayLat, AwayLon)
            ]
        };

        await Service(client).BuildDayAsync(Plate, Day, CancellationToken.None);

        Assert.Equal(2, _context.VehicleDayRollups.Single().IgnitionCycleCount);
    }

    // — Odometer ——————————————————————————————————————————————————————

    [Fact]
    public async Task The_providers_own_distance_is_stored_never_the_difference()
    {
        // Observed on this fleet: distance 0 with an end reading BELOW the start. Subtracting
        // would store a negative day.
        AddRouteDay();

        var client = new StubClient
        {
            Odometer = new CartrackOdometerSummary
            {
                StartOdometerMetres = 387_555_800,
                EndOdometerMetres = 387_551_500,
                DistanceMetres = 0
            }
        };

        await Service(client).BuildDayAsync(Plate, Day, CancellationToken.None);

        var row = Assert.Single(_context.VehicleDayRollups);

        Assert.Equal(0, row.DistanceMetres);
        Assert.Equal(387_555_800, row.OdometerStartMetres);
        Assert.True(row.HasOdometer);
    }

    [Fact]
    public async Task A_negative_distance_is_refused_rather_than_stored()
    {
        AddRouteDay();

        await Service(new StubClient
        {
            Odometer = new CartrackOdometerSummary { DistanceMetres = -4300 }
        }).BuildDayAsync(Plate, Day, CancellationToken.None);

        Assert.Null(_context.VehicleDayRollups.Single().DistanceMetres);
    }

    [Fact]
    public async Task The_reset_and_terminal_change_flags_are_carried_through()
    {
        // Both mean the distance across the window is meaningless, and the report has to be able
        // to say so rather than print a number.
        AddRouteDay();

        await Service(new StubClient
        {
            Odometer = new CartrackOdometerSummary
            {
                DistanceMetres = 67_100,
                OdometerReset = true,
                TerminalHasChanged = true
            }
        }).BuildDayAsync(Plate, Day, CancellationToken.None);

        var row = Assert.Single(_context.VehicleDayRollups);

        Assert.True(row.OdometerWasReset);
        Assert.True(row.TerminalChanged);
    }

    // — Partial failure ——————————————————————————————————————————————

    [Fact]
    public async Task A_failing_odometer_does_not_discard_the_movement_already_read()
    {
        AddRouteDay();

        var client = new StubClient
        {
            Events = [Event("IGNITION_ON", 6, 20, DepotLat, DepotLon)],
            OdometerThrows = new HttpRequestException("odometer is down")
        };

        await Service(client).BuildDayAsync(Plate, Day, CancellationToken.None);

        var row = Assert.Single(_context.VehicleDayRollups);

        Assert.True(row.HasActivity);
        Assert.NotNull(row.FirstIgnitionOnUtc);
        Assert.False(row.HasOdometer);
        Assert.Contains("odometer is down", row.LastError);
    }

    [Fact]
    public async Task A_failing_movement_read_is_not_recorded_as_a_quiet_day()
    {
        // The distinction the whole provenance scheme exists for: HasActivity false means we did
        // not find out, which the report must not render as a van that sat still.
        AddRouteDay();

        var client = new StubClient { EventsThrow = new HttpRequestException("events are down") };

        await Service(client).BuildDayAsync(Plate, Day, CancellationToken.None);

        var row = Assert.Single(_context.VehicleDayRollups);

        Assert.False(row.HasActivity);
        Assert.Null(row.FirstIgnitionOnUtc);
        Assert.NotNull(row.LastError);
        Assert.False(row.IsComplete);
    }

    [Fact]
    public async Task A_failed_re_read_does_not_downgrade_a_day_that_was_already_built()
    {
        // Found by running this overnight. The laptop lost its network, the hourly pass re-read
        // four days that already had real figures — one of them a 356 km round — and every
        // provenance flag went false, which the report renders as "the van did not report".
        //
        // A day we have is not a day we lack, however recently we last confirmed it.
        AddRouteDay();

        var client = new StubClient
        {
            Events =
            [
                Event("IGNITION_ON", 6, 20, DepotLat, DepotLon),
                Event("POSITION", 7, 5, AwayLat, AwayLon)
            ],
            Odometer = new CartrackOdometerSummary { DistanceMetres = 356_700 }
        };

        await Service(client).BuildDayAsync(Plate, Day, CancellationToken.None);

        // The provider goes away, and the reconciliation pass comes round again.
        client.EventsThrow = new HttpRequestException("No such host is known.");
        client.OdometerThrows = new HttpRequestException("No such host is known.");

        await Service(client).BuildDayAsync(Plate, Day, CancellationToken.None);

        var row = Assert.Single(_context.VehicleDayRollups);

        Assert.True(row.HasActivity);
        Assert.True(row.HasOdometer);
        Assert.Equal(At(7, 5), row.FirstDepartureUtc);
        Assert.Equal(356_700, row.DistanceMetres);

        // The failure is still recorded — it just does not erase the day.
        Assert.Contains("No such host", row.LastError);
    }

    [Fact]
    public async Task A_day_that_was_never_built_stays_incomplete_after_a_failure()
    {
        // The other half of the rule above: leaving the flag alone must not mean a day that has
        // never been read looks complete.
        AddRouteDay();

        var client = new StubClient
        {
            EventsThrow = new HttpRequestException("No such host is known."),
            OdometerThrows = new HttpRequestException("No such host is known.")
        };

        await Service(client).BuildDayAsync(Plate, Day, CancellationToken.None);

        var row = Assert.Single(_context.VehicleDayRollups);

        Assert.False(row.HasActivity);
        Assert.False(row.HasOdometer);
        Assert.False(row.IsComplete);
    }

    [Fact]
    public async Task A_van_that_reported_nothing_is_a_complete_day_with_no_movement()
    {
        // The other side of the same coin: the reads worked and the van said nothing.
        AddRouteDay();

        await Service(new StubClient { Odometer = new CartrackOdometerSummary() })
            .BuildDayAsync(Plate, Day, CancellationToken.None);

        var row = Assert.Single(_context.VehicleDayRollups);

        Assert.True(row.HasActivity);
        Assert.True(row.HasOdometer);
        Assert.Null(row.FirstIgnitionOnUtc);
    }

    [Fact]
    public async Task A_rate_limit_stops_the_pass_rather_than_writing_an_empty_day()
    {
        AddRouteDay();

        var client = new StubClient
        {
            EventsThrow = new CartrackRateLimitedException(TimeSpan.FromMinutes(10), "vehicles/events")
        };

        await Assert.ThrowsAsync<CartrackRateLimitedException>(
            () => Service(client).BuildDayAsync(Plate, Day, CancellationToken.None));
    }

    // — Idempotency ————————————————————————————————————————————————————

    [Fact]
    public async Task Rebuilding_a_day_updates_the_row_rather_than_adding_a_second()
    {
        AddRouteDay();

        var client = new StubClient { Events = [Event("IGNITION_ON", 6, 20, DepotLat, DepotLon)] };

        await Service(client).BuildDayAsync(Plate, Day, CancellationToken.None);

        client.Events = [Event("IGNITION_ON", 5, 50, DepotLat, DepotLon)];

        await Service(client).BuildDayAsync(Plate, Day, CancellationToken.None);

        var row = Assert.Single(_context.VehicleDayRollups);

        Assert.Equal(At(5, 50), row.FirstIgnitionOnUtc);
        Assert.Equal(2, row.AttemptCount);
    }

    // — The pass ——————————————————————————————————————————————————————

    [Fact]
    public async Task A_sync_builds_the_route_default_truck_even_with_no_route_day()
    {
        // The case the report most wants to catch: the rep never tapped Start Day, so there is no
        // handset record at all — and the vehicle can still be checked.
        var built = await Service(new StubClient()).SyncAsync(CartrackRollupPass.Nightly, CancellationToken.None);

        Assert.True(built > 0);
        Assert.All(_context.VehicleDayRollups, row => Assert.Equal(Plate, row.RegistrationNormalized));
    }

    [Fact]
    public async Task A_sync_records_itself_and_its_checkpoint()
    {
        await Service(new StubClient()).SyncAsync(CartrackRollupPass.Nightly, CancellationToken.None);

        var state = Assert.Single(
            _context.CacheSyncStates.Where(s => s.CacheKey == CartrackRollupService.CacheKey));

        Assert.NotNull(state.LastSyncedAt);
        Assert.Null(state.LastError);

        var checkpoint = Assert.Single(
            _context.SystemConfigs.Where(c => c.Key == "Cartrack.Rollup.Checkpoint"));

        Assert.False(string.IsNullOrWhiteSpace(checkpoint.Value));
    }

    [Fact]
    public async Task A_reconciliation_that_could_not_reach_the_provider_is_not_marked_done()
    {
        // It runs once a day. Marking a failed pass done costs a full day before anything tries
        // again, which is a long time to leave a corrected figure unread.
        var client = new StubClient { EventsThrow = new HttpRequestException("No such host is known.") };

        await Service(client).SyncAsync(CartrackRollupPass.Nightly, CancellationToken.None);

        var checkpoint = ReadCheckpoint();

        Assert.Null(checkpoint.LastReconciledAtUtc);
    }

    [Fact]
    public async Task A_reconciliation_that_reached_the_provider_is_marked_done()
    {
        await Service(new StubClient()).SyncAsync(CartrackRollupPass.Nightly, CancellationToken.None);

        Assert.NotNull(ReadCheckpoint().LastReconciledAtUtc);
    }

    [Fact]
    public async Task The_backfill_stops_where_the_reading_stopped()
    {
        // Walking past a date the provider would not answer for would mark it backfilled and
        // never come back, and the readiness gate would then claim a covered range with a hole.
        var client = new StubClient { EventsThrow = new HttpRequestException("No such host is known.") };

        await Service(client).SyncAsync(CartrackRollupPass.Nightly, CancellationToken.None);

        var checkpoint = ReadCheckpoint();

        Assert.False(checkpoint.BackfillCompleted);
    }

    // — What each pass costs ——————————————————————————————————————————

    [Fact]
    public async Task An_intraday_pass_reads_only_todays_fleet_wide_movement()
    {
        // The hourly pass runs fifteen times a day. Per-vehicle reads there would cost three
        // requests per van per hour against a rate limit shared with Cartrack's other customers.
        var client = new StubClient();

        await Service(client).SyncAsync(CartrackRollupPass.Intraday, CancellationToken.None);

        Assert.Equal(["activity", "events"], client.Calls);

        var today = CartrackTime.TradingDateOf(DateTime.UtcNow);
        var row = Assert.Single(_context.VehicleDayRollups);

        Assert.Equal(today, row.TradingDate);
        Assert.False(row.HasOdometer);
    }

    [Fact]
    public async Task An_intraday_pass_neither_backfills_nor_reconciles()
    {
        await Service(new StubClient()).SyncAsync(CartrackRollupPass.Intraday, CancellationToken.None);

        var checkpoint = ReadCheckpoint();

        Assert.False(checkpoint.BackfillCompleted);
        Assert.Null(checkpoint.LastReconciledAtUtc);
        Assert.Equal(CartrackTime.TradingDateOf(DateTime.UtcNow), checkpoint.LastBuiltDate);
    }

    [Fact]
    public async Task An_intraday_refresh_keeps_what_a_full_build_already_read()
    {
        // A movement-only pass leaves the odometer alone. Turning HasOdometer off because this
        // pass did not ask would tell the report the reading had failed.
        var today = CartrackTime.TradingDateOf(DateTime.UtcNow);
        var client = new StubClient
        {
            Odometer = new CartrackOdometerSummary
            {
                StartOdometerMetres = 100_000,
                EndOdometerMetres = 142_000,
                DistanceMetres = 42_000
            }
        };

        await Service(client).BuildDayAsync(Plate, today, CancellationToken.None);
        await Service(new StubClient()).SyncAsync(CartrackRollupPass.Intraday, CancellationToken.None);

        var row = await _context.VehicleDayRollups.AsNoTracking().SingleAsync();

        Assert.True(row.HasOdometer);
        Assert.Equal(42_000, row.DistanceMetres);
        Assert.Equal(2, row.AttemptCount);
    }

    [Fact]
    public async Task A_nightly_pass_builds_yesterday_and_leaves_today_to_the_hourly_pass()
    {
        var client = new StubClient();

        await Service(client, s => s.BackfillDays = 1).SyncAsync(
            CartrackRollupPass.Nightly, CancellationToken.None);

        var today = CartrackTime.TradingDateOf(DateTime.UtcNow);
        var dates = _context.VehicleDayRollups.Select(row => row.TradingDate).ToList();

        Assert.DoesNotContain(today, dates);
        Assert.Contains(today.AddDays(-1), dates);
        Assert.Contains("odometer", client.Calls);
        Assert.Equal(today.AddDays(-1), ReadCheckpoint().LastBuiltDate);
    }

    [Fact]
    public async Task A_nightly_pass_never_moves_the_built_date_backwards()
    {
        // The readiness gate reads LastBuiltDate as the newest day it may show; the nightly pass
        // building yesterday must not hide the today an hourly pass already built.
        await Service(new StubClient()).SyncAsync(CartrackRollupPass.Intraday, CancellationToken.None);
        await Service(new StubClient()).SyncAsync(CartrackRollupPass.Nightly, CancellationToken.None);

        Assert.Equal(CartrackTime.TradingDateOf(DateTime.UtcNow), ReadCheckpoint().LastBuiltDate);
    }

    [Theory]
    [InlineData(5, 19, 1, "0 10 5-19 * * ?", 15)]
    [InlineData(5, 19, 2, "0 10 5-19/2 * * ?", 8)]
    [InlineData(4, 20, 1, "0 10 4-20 * * ?", 17)]
    [InlineData(5, 19, 0, "0 10 5-19 * * ?", 15)]
    [InlineData(20, 4, 1, "0 10 5-19 * * ?", 15)]
    [InlineData(-1, 19, 1, "0 10 5-19 * * ?", 15)]
    [InlineData(5, 24, 1, "0 10 5-19 * * ?", 15)]
    public void The_intraday_schedule_covers_the_trading_hours_only(
        int from, int to, int every, string expected, int passes)
    {
        var settings = new CartrackSettings
        {
            IntradayRollupFromHourCat = from,
            IntradayRollupToHourCat = to,
            IntradayRollupEveryHours = every
        };

        var cron = QuartzConfiguration.BuildIntradayRollupCron(settings);

        Assert.Equal(expected, cron);
        Assert.True(Quartz.CronExpression.IsValidExpression(cron));

        // The reserve the nightly pass leaves is sized from this, so it must count what the
        // schedule actually fires: every firing of the cron inside one day.
        var expression = new Quartz.CronExpression(cron) { TimeZone = TimeZoneInfo.Utc };
        var fired = 0;

        for (var at = expression.GetTimeAfter(new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero));
             at is { } next && next < new DateTimeOffset(2026, 9, 22, 0, 0, 0, TimeSpan.Zero);
             at = expression.GetTimeAfter(next))
        {
            fired++;
        }

        Assert.Equal(passes, fired);
        Assert.Equal(passes, settings.IntradayPassesPerDay);
    }

    // — The daily request budget ————————————————————————————————————————

    [Fact]
    public async Task A_pass_that_runs_out_of_budget_stops_cleanly_and_counts_what_it_spent()
    {
        // One request left: the activity call is made, the events call is refused. That is a
        // stopped pass, not a failed one — no exception, no error on the status page.
        var client = new StubClient();

        await Service(client, s => s.MaxRequestsPerDay = 1)
            .SyncAsync(CartrackRollupPass.Intraday, CancellationToken.None);

        Assert.Equal(["activity"], client.Calls);

        var checkpoint = ReadCheckpoint();

        Assert.Equal(1, checkpoint.RequestsUsed);
        Assert.Equal(CartrackTime.TradingDateOf(DateTime.UtcNow), checkpoint.RequestsDate);
        Assert.Null(checkpoint.LastBuiltDate);
        Assert.Null(_context.CacheSyncStates.Single(s => s.CacheKey == CartrackRollupService.CacheKey).LastError);
    }

    [Fact]
    public async Task The_daily_budget_holds_across_passes()
    {
        // Counted in the checkpoint, not in memory, so a new job run — or another node — sees
        // what the day has already spent.
        void Cap(CartrackSettings s) => s.MaxRequestsPerDay = 3;

        var first = new StubClient();
        await Service(first, Cap).SyncAsync(CartrackRollupPass.Intraday, CancellationToken.None);

        var second = new StubClient();
        await Service(second, Cap).SyncAsync(CartrackRollupPass.Intraday, CancellationToken.None);

        var third = new StubClient();
        await Service(third, Cap).SyncAsync(CartrackRollupPass.Intraday, CancellationToken.None);

        Assert.Equal(2, first.Calls.Count);
        Assert.Single(second.Calls);
        Assert.Empty(third.Calls);
        Assert.Equal(3, ReadCheckpoint().RequestsUsed);
    }

    [Fact]
    public async Task A_nightly_pass_leaves_the_intraday_passes_their_share()
    {
        // Eight intraday passes at four requests each: a budget no bigger than that reserve gives
        // the nightly pass nothing, so history can never crowd out this morning's departures.
        var client = new StubClient();

        await Service(client, s =>
            {
                s.IntradayRollupEveryHours = 2;
                s.MaxRequestsPerDay = 8 * CartrackSettings.RequestsPerIntradayPass;
            })
            .SyncAsync(CartrackRollupPass.Nightly, CancellationToken.None);

        Assert.Empty(client.Calls);
        Assert.Empty(_context.VehicleDayRollups);
    }

    [Fact]
    public async Task A_nightly_pass_spends_on_yesterday_first_and_backfill_last()
    {
        // One van, four requests a date: yesterday, one reconciliation day, one backfill date, and
        // two requests into a second backfill date before the budget runs out. The backfill keeps
        // the date it finished and not the one it was part-way through.
        var client = new StubClient();
        var today = CartrackTime.TradingDateOf(DateTime.UtcNow);

        await Service(client, s =>
            {
                s.IntradayRollupEveryHours = 2;
                s.ReconciliationWindowDays = 2;
                s.BackfillDays = 3;
                s.MaxBackfillDaysPerRun = 2;
                s.MaxRequestsPerDay = 8 * CartrackSettings.RequestsPerIntradayPass + 4 + 4 + 4 + 2;
            })
            .SyncAsync(CartrackRollupPass.Nightly, CancellationToken.None);

        var checkpoint = ReadCheckpoint();

        Assert.Equal(14, checkpoint.RequestsUsed);
        Assert.Equal(today.AddDays(-1), checkpoint.LastBuiltDate);
        Assert.NotNull(checkpoint.LastReconciledAtUtc);
        Assert.Equal(today.AddDays(-3), checkpoint.BackfillThroughDate);
        Assert.False(checkpoint.BackfillCompleted);
    }

    private CartrackRollupCheckpoint ReadCheckpoint()
    {
        var stored = _context.SystemConfigs
            .AsNoTracking()
            .Single(config => config.Key == "Cartrack.Rollup.Checkpoint")
            .Value;

        return System.Text.Json.JsonSerializer.Deserialize<CartrackRollupCheckpoint>(stored!)!;
    }

    [Fact]
    public async Task Nothing_runs_when_the_integration_is_off()
    {
        var built = await Service(new StubClient(), s => s.Enabled = false)
            .SyncAsync(CartrackRollupPass.Nightly, CancellationToken.None);

        Assert.Equal(0, built);
        Assert.Empty(_context.VehicleDayRollups);
    }

    [Fact]
    public async Task An_unreadable_checkpoint_is_reset_rather_than_fatal()
    {
        _context.SystemConfigs.Add(new SystemConfigEntity
        {
            Key = "Cartrack.Rollup.Checkpoint",
            Value = "{ this is not json",
            ValueType = "json",
            Category = "Synchronization",
            IsEditable = false
        });

        await _context.SaveChangesAsync();

        var built = await Service(new StubClient()).SyncAsync(CartrackRollupPass.Nightly, CancellationToken.None);

        Assert.True(built > 0);
    }

    // — The readiness gate ————————————————————————————————————————————

    [Fact]
    public async Task The_gate_refuses_a_period_the_backfill_has_not_reached()
    {
        // A backfill that has reached September cannot speak for July. Showing July as "the van
        // never moved" reads as a finding, which is worse than showing nothing.
        await Service(new StubClient()).SyncAsync(CartrackRollupPass.Nightly, CancellationToken.None);

        var status = await Reader().GetStatusAsync(
            new DateTime(2026, 7, 1), new DateTime(2026, 7, 31), CancellationToken.None);

        Assert.False(status.Ready);
        Assert.NotNull(status.Reason);
        Assert.Contains("cannot speak for this whole period", status.Reason);

        // Read it as a sentence, not as a template. An earlier draft produced "Fleet telematics
        // has back to 18 Sept 2026" and shipped it to the screen.
        Assert.DoesNotContain("has back to", status.Reason);
        Assert.Contains("only reaches back to", status.Reason);
    }

    [Fact]
    public async Task The_gate_explains_being_switched_off_differently_from_being_empty()
    {
        var off = await Reader(s => s.Enabled = false)
            .GetStatusAsync(Day, Day, CancellationToken.None);

        Assert.False(off.Enabled);
        Assert.Contains("switched off", off.Reason);

        var uncredentialed = await Reader(s => s.Password = string.Empty)
            .GetStatusAsync(Day, Day, CancellationToken.None);

        Assert.True(uncredentialed.Enabled);
        Assert.False(uncredentialed.Configured);
        Assert.Contains("no credentials", uncredentialed.Reason);

        var neverRun = await Reader().GetStatusAsync(Day, Day, CancellationToken.None);

        Assert.True(neverRun.Configured);
        Assert.Contains("has not run yet", neverRun.Reason);
    }

    [Fact]
    public async Task The_gate_refuses_a_stale_rollup()
    {
        await Service(new StubClient()).SyncAsync(CartrackRollupPass.Nightly, CancellationToken.None);

        var state = _context.CacheSyncStates.Single(s => s.CacheKey == CartrackRollupService.CacheKey);
        state.LastSyncedAt = DateTime.UtcNow.AddDays(-4);
        await _context.SaveChangesAsync();

        var status = await Reader().GetStatusAsync(Day, Day, CancellationToken.None);

        Assert.False(status.Ready);
        Assert.Contains("may be out of date", status.Reason);
    }

    private sealed class StubClient : ICartrackClient
    {
        public IReadOnlyList<CartrackVehicleEvent> Events { get; set; } = [];
        public IReadOnlyList<CartrackVehicleActivity> Activity { get; set; } = [];
        public CartrackOdometerSummary? Odometer { get; set; }
        public Exception? EventsThrow { get; set; }
        public Exception? OdometerThrows { get; set; }

        /// <summary>Every call the rollup made, by endpoint — what a pass costs against the rate limit.</summary>
        public List<string> Calls { get; } = [];

        /// <summary>The run's request budget, spent on every call the way the real client spends it.</summary>
        public CartrackRequestBudget Budget { get; } = new();

        private void Spend(string endpoint)
        {
            Budget.Take(endpoint);
            Calls.Add(endpoint);
        }

        public Task<IReadOnlyList<CartrackVehicle>> GetVehiclesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CartrackVehicle>>([]);

        public Task<IReadOnlyList<CartrackVehicleActivity>> GetActivityAsync(
            DateTime tradingDate, CancellationToken cancellationToken)
        {
            Spend("activity");

            return EventsThrow is not null
                ? Task.FromException<IReadOnlyList<CartrackVehicleActivity>>(EventsThrow)
                : Task.FromResult(Activity);
        }

        public Task<IReadOnlyList<CartrackVehicleEvent>> GetEventsAsync(
            DateTime fromUtc, DateTime toUtc, string? registration, CancellationToken cancellationToken)
        {
            Spend("events");

            return EventsThrow is not null
                ? Task.FromException<IReadOnlyList<CartrackVehicleEvent>>(EventsThrow)
                : Task.FromResult(Events);
        }

        public Task<IReadOnlyList<CartrackTemperatureReading>> GetTemperaturesAsync(
            DateTime fromUtc, DateTime toUtc, string? registration, CancellationToken cancellationToken)
        {
            Spend("temperatures");

            return Task.FromResult<IReadOnlyList<CartrackTemperatureReading>>([]);
        }

        public Task<CartrackOdometerSummary?> GetOdometerAsync(
            string registration, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken)
        {
            Spend("odometer");

            return OdometerThrows is not null
                ? Task.FromException<CartrackOdometerSummary?>(OdometerThrows)
                : Task.FromResult(Odometer);
        }

        public Task<CartrackFuelConsumed?> GetFuelConsumedAsync(
            string registration, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken) =>
            Task.FromResult<CartrackFuelConsumed?>(null);

        public Task<CartrackFuelLevel?> GetFuelLevelAsync(
            string registration, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken) =>
            Task.FromResult<CartrackFuelLevel?>(null);

        public Task<IReadOnlyList<CartrackFuelFill>> GetFuelFillsAsync(
            string registration, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CartrackFuelFill>>([]);

        public Task<IReadOnlyList<CartrackVehicleStatus>> GetStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CartrackVehicleStatus>>([]);
    }
}
