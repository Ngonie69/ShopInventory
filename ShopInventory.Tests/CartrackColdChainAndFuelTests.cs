using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;
using ShopInventory.Models.Telematics;
using ShopInventory.Services;
using ShopInventory.Services.Telematics;
using ShopInventory.Web.Models;
using Xunit;

namespace ShopInventory.Tests;

/// <summary>
/// The fuel and cold-chain facets of the day rollup, the arithmetic behind a breach, and the words
/// the fleet page puts on each.
/// </summary>
/// <remarks>
/// The facts worth pinning are the ones that would turn into a false accusation or a false
/// reassurance: a reporting gap counted as time spent warm, widening a route's limits quietly
/// erasing a breach, and an empty cell that does not say which kind of empty it is.
/// </remarks>
public class CartrackColdChainAndFuelTests : IDisposable
{
    private const string Plate = "AFQ9644";
    private static readonly DateTime Day = new(2026, 9, 19);
    private static readonly TimeSpan Cap = TimeSpan.FromMinutes(30);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public CartrackColdChainAndFuelTests()
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
            IsActive = true,
            TemperatureMinC = -18m,
            TemperatureMaxC = -12m
        });

        _context.TelematicsVehicles.Add(new TelematicsVehicleEntity
        {
            RegistrationNormalized = Plate,
            Registration = Plate,
            IsActiveInFleet = true,
            HasFuelAnalogLevel = true
        });

        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private CartrackRollupService Service(StubClient client) =>
        new(_context,
            client,
            new CartrackRequestBudget(),
            Options.Create(new CartrackSettings
            {
                Enabled = true,
                Username = "u",
                Password = "p",
                TemperatureSampleGapCapMinutes = 30
            }),
            NullLogger<CartrackRollupService>.Instance);

    private static DateTime At(int hour, int minute) =>
        AuditService.FromCAT(Day.AddHours(hour).AddMinutes(minute));

    private static CartrackTemperatureReading Reading(
        int hour, int minute, decimal? temp1, decimal? temp2 = null, string registration = Plate) => new()
        {
            Registration = registration,
            EventTs = Day.AddHours(hour).AddMinutes(minute).ToString("yyyy-MM-dd HH:mm:ss") + "+02",
            Temp1 = temp1,
            Temp2 = temp2
        };

    private async Task<VehicleDayRollupEntity> BuildAsync(StubClient client)
    {
        await Service(client).BuildDayAsync(Plate, Day, CancellationToken.None);

        _context.ChangeTracker.Clear();

        return await _context.VehicleDayRollups.SingleAsync();
    }

    // — The arithmetic ————————————————————————————————————————————————

    [Fact]
    public void A_reading_holds_until_the_next_one()
    {
        // 08:00 in range, 08:15 at -5 (above the -12 ceiling), 08:30 back in range: fifteen
        // minutes warm, attributed to the reading that opened the interval.
        var summary = ColdChainEvaluator.Evaluate(
        [
            new(At(8, 0), -15m),
            new(At(8, 15), -5m),
            new(At(8, 30), -15m)
        ], -18m, -12m, Cap);

        Assert.Equal(15, summary.MinutesAboveMax);
        Assert.Equal(0, summary.MinutesBelowMin);
    }

    [Fact]
    public void A_reporting_gap_is_not_counted_as_time_spent_warm()
    {
        // One warm reading, then four hours of silence. Nothing was measured in between, so it
        // is half an hour of evidence, not a four-hour breach.
        var summary = ColdChainEvaluator.Evaluate(
        [
            new(At(8, 0), 12m),
            new(At(12, 0), -15m)
        ], -18m, -12m, Cap);

        Assert.Equal(30, summary.MinutesAboveMax);
    }

    [Fact]
    public void The_last_reading_of_the_day_counts_nothing()
    {
        var summary = ColdChainEvaluator.Evaluate(
        [
            new(At(8, 0), -15m),
            new(At(8, 10), 20m)
        ], -18m, -12m, Cap);

        Assert.Equal(0, summary.MinutesAboveMax);
        Assert.Equal(20m, summary.MaxC);
    }

    [Fact]
    public void Too_cold_is_counted_separately_from_too_warm()
    {
        var summary = ColdChainEvaluator.Evaluate(
        [
            new(At(8, 0), -25m),
            new(At(8, 20), -15m)
        ], -18m, -12m, Cap);

        Assert.Equal(20, summary.MinutesBelowMin);
        Assert.Equal(0, summary.MinutesAboveMax);
    }

    [Fact]
    public void A_round_with_no_limits_is_not_judged_at_all()
    {
        // Null, not zero. Zero would read as "stayed in range", which nobody asked it to.
        var summary = ColdChainEvaluator.Evaluate(
        [
            new(At(8, 0), 30m),
            new(At(8, 20), 30m)
        ], null, null, Cap);

        Assert.Null(summary.MinutesAboveMax);
        Assert.Null(summary.MinutesBelowMin);
        Assert.Equal(2, summary.SampleCount);
    }

    [Fact]
    public void Silence_on_a_judged_round_is_zero_minutes_and_zero_samples()
    {
        var summary = ColdChainEvaluator.Evaluate([], -18m, -12m, Cap);

        Assert.Equal(0, summary.SampleCount);
        Assert.Equal(0, summary.MinutesAboveMax);
        Assert.Null(summary.MinC);
    }

    // — The temperature facet —————————————————————————————————————————

    [Fact]
    public async Task Every_reporting_channel_is_kept_as_evidence_and_nothing_is_stored_twice()
    {
        var client = new StubClient
        {
            Temperatures =
            [
                Reading(8, 0, -15m, 4m),
                Reading(8, 15, -14m),
                Reading(8, 30, -13m)
            ]
        };

        await BuildAsync(client);
        await BuildAsync(client);

        // Four samples: three on channel 1, one on channel 2. Nulls on the other channels are
        // not stored, and the second build added nothing.
        Assert.Equal(4, await _context.VehicleTemperatureSamples.CountAsync());
        Assert.Equal(3, await _context.VehicleTemperatureSamples.CountAsync(s => s.Channel == 1));
    }

    [Fact]
    public async Task Readings_for_another_vehicle_are_ignored()
    {
        var row = await BuildAsync(new StubClient
        {
            Temperatures = [Reading(8, 0, -15m), Reading(8, 15, 20m, registration: "ACQ3455")]
        });

        Assert.Equal(1, row.TemperatureSampleCount);
        Assert.Equal(1, await _context.VehicleTemperatureSamples.CountAsync());
    }

    [Fact]
    public async Task The_day_is_judged_against_the_route_limits_and_keeps_them()
    {
        var client = new StubClient
        {
            Temperatures = [Reading(8, 0, -5m), Reading(8, 20, -15m)]
        };

        var first = await BuildAsync(client);

        Assert.Equal(-18m, first.LimitMinC);
        Assert.Equal(-12m, first.LimitMaxC);
        Assert.Equal(20, first.MinutesAboveMaxLimit);

        // Somebody widens the limits the next morning. The day was judged against what was in
        // force, and a rebuild must not quietly erase the breach.
        var route = await _context.Routes.SingleAsync();
        route.TemperatureMaxC = 0m;
        await _context.SaveChangesAsync();

        var rebuilt = await BuildAsync(client);

        Assert.Equal(-12m, rebuilt.LimitMaxC);
        Assert.Equal(20, rebuilt.MinutesAboveMaxLimit);
    }

    [Fact]
    public async Task A_day_built_before_its_route_had_limits_takes_them_up_on_a_rebuild()
    {
        var route = await _context.Routes.SingleAsync();
        route.TemperatureMinC = null;
        route.TemperatureMaxC = null;
        await _context.SaveChangesAsync();

        var client = new StubClient { Temperatures = [Reading(8, 0, -5m), Reading(8, 20, -15m)] };

        var unjudged = await BuildAsync(client);
        Assert.Null(unjudged.MinutesAboveMaxLimit);

        route = await _context.Routes.SingleAsync();
        route.TemperatureMinC = -18m;
        route.TemperatureMaxC = -12m;
        await _context.SaveChangesAsync();

        var judged = await BuildAsync(client);

        Assert.Equal(-12m, judged.LimitMaxC);
        Assert.Equal(20, judged.MinutesAboveMaxLimit);
    }

    [Fact]
    public async Task The_route_chooses_the_probe_when_it_names_one()
    {
        var route = await _context.Routes.SingleAsync();
        route.TemperatureProbeChannel = 2;
        await _context.SaveChangesAsync();

        var row = await BuildAsync(new StubClient
        {
            Temperatures = [Reading(8, 0, 25m, -15m), Reading(8, 20, 25m, -16m)]
        });

        Assert.Equal((byte)2, row.TemperatureChannel);
        Assert.Equal(-16m, row.TemperatureMinC);
        Assert.Equal(0, row.MinutesAboveMaxLimit);
    }

    [Fact]
    public async Task Otherwise_the_lowest_reporting_channel_is_the_fridge()
    {
        var row = await BuildAsync(new StubClient
        {
            Temperatures = [Reading(8, 0, null, -15m), Reading(8, 20, null, -16m)]
        });

        Assert.Equal((byte)2, row.TemperatureChannel);
        Assert.Equal(2, row.TemperatureSampleCount);
    }

    [Fact]
    public async Task A_vehicle_that_reports_a_reading_is_known_to_have_a_probe()
    {
        await BuildAsync(new StubClient { Temperatures = [Reading(8, 0, -15m)] });

        var vehicle = await _context.TelematicsVehicles.SingleAsync();

        Assert.True(vehicle.HasTemperatureProbe);
        Assert.Equal(At(8, 0), vehicle.LastTemperatureSeenAtUtc);
    }

    [Fact]
    public async Task A_silent_day_does_not_take_the_probe_away()
    {
        // The fridge was off, which is what the live probe showed on 19 September. The probe did
        // not come out of the truck.
        var vehicle = await _context.TelematicsVehicles.SingleAsync();
        vehicle.HasTemperatureProbe = true;
        await _context.SaveChangesAsync();

        var row = await BuildAsync(new StubClient());

        Assert.True((await _context.TelematicsVehicles.SingleAsync()).HasTemperatureProbe);
        Assert.True(row.HasTemperature);
        Assert.Equal(0, row.TemperatureSampleCount);
    }

    [Fact]
    public async Task A_failed_temperature_read_keeps_the_rest_of_the_day()
    {
        var row = await BuildAsync(new StubClient
        {
            Odometer = new CartrackOdometerSummary { DistanceMetres = 67_100 },
            TemperatureThrows = new HttpRequestException("503")
        });

        Assert.False(row.HasTemperature);
        Assert.True(row.HasOdometer);
        Assert.True(row.HasActivity);
        Assert.Equal(67_100, row.DistanceMetres);
    }

    [Fact]
    public async Task Today_is_read_up_to_now_rather_than_to_tonight()
    {
        // Found live: the endpoint answers 422 to a window ending in the future, so asking for
        // the whole of today meant today was never read at all.
        var today = AuditService.ToCAT(DateTime.UtcNow).Date;
        var client = new StubClient();

        await Service(client).BuildDayAsync(Plate, today, CancellationToken.None);

        Assert.NotNull(client.TemperatureAskedTo);
        Assert.True(client.TemperatureAskedTo <= DateTime.UtcNow);
    }

    // — The fuel facet ————————————————————————————————————————————————

    [Fact]
    public async Task A_vehicle_with_no_fuel_sensor_is_not_asked()
    {
        var vehicle = await _context.TelematicsVehicles.SingleAsync();
        vehicle.HasFuelAnalogLevel = false;
        await _context.SaveChangesAsync();

        var client = new StubClient();
        var row = await BuildAsync(client);

        Assert.Equal(0, client.FuelCalls);
        Assert.False(row.HasFuel);
    }

    [Fact]
    public async Task Each_fuel_call_is_gated_on_the_sensor_that_answers_it()
    {
        // An analog sender reads the tank level. It cannot report engine consumption — that needs
        // a CAN read — so that call is not made.
        var client = new StubClient
        {
            FuelLevel = new CartrackFuelLevel
            {
                Start = new CartrackFuelLevelReading { Litres = 41.12m, IsAccurate = true },
                End = new CartrackFuelLevelReading { Litres = 41.08m, IsAccurate = false },
                EstimatedFuelUsedLitres = 0.04m,
                IsCalibrated = true
            },
            Fills = [new CartrackFuelFill { Litres = 80m }]
        };

        var row = await BuildAsync(client);

        Assert.Equal(0, client.ConsumedCalls);
        Assert.True(row.HasFuel);
        Assert.Equal(41.12m, row.FuelLevelStartLitres);
        Assert.Equal(0.04m, row.EstimatedFuelUsedLitres);
        Assert.True(row.FuelIsCalibrated);
        Assert.Equal(1, row.FuelFillCount);
        Assert.Equal(80m, row.FuelFilledLitres);

        // One end unsettled makes the pair unsettled. That is the live AFQ9644 day exactly.
        Assert.False(row.FuelReadingsAccurate);
    }

    [Fact]
    public async Task A_fill_the_provider_lists_twice_is_counted_once()
    {
        // Seen live on 14 Sep: one 121.9 L fill, returned twice. Counted twice it is 243.8 L into
        // a tank that took 95.
        var fill = new CartrackFuelFill
        {
            Litres = 121.9m,
            EventTs = "2026-09-14 08:14:18+02",
            IsAccurate = true
        };

        var row = await BuildAsync(new StubClient
        {
            FuelLevel = new CartrackFuelLevel
            {
                Start = new CartrackFuelLevelReading { Litres = 71.11m, IsAccurate = true },
                End = new CartrackFuelLevelReading { Litres = 166.33m, IsAccurate = true },
                IsCalibrated = true
            },
            Fills = [fill, new CartrackFuelFill { Litres = 121.9m, EventTs = fill.EventTs, IsAccurate = true }]
        });

        Assert.Equal(1, row.FuelFillCount);
        Assert.Equal(121.9m, row.FuelFilledLitres);
        Assert.True(row.FuelReadingsAccurate);
    }

    [Fact]
    public async Task A_level_response_with_no_readings_does_not_speak_for_calibration()
    {
        // The live morning of 21 Sep: no readings yet, and "calibrated": false on a sender that
        // was calibrated on every day it had readings.
        var row = await BuildAsync(new StubClient
        {
            FuelLevel = new CartrackFuelLevel { IsCalibrated = false }
        });

        Assert.True(row.HasFuel);
        Assert.Null(row.FuelIsCalibrated);
    }

    [Fact]
    public async Task An_unsettled_fill_makes_the_day_provisional()
    {
        // The live 20 Sep: both tank levels settled, the fills between them not yet.
        var row = await BuildAsync(new StubClient
        {
            FuelLevel = new CartrackFuelLevel
            {
                Start = new CartrackFuelLevelReading { Litres = 41.08m, IsAccurate = true },
                End = new CartrackFuelLevelReading { Litres = 173.06m, IsAccurate = true },
                IsCalibrated = true
            },
            Fills = [new CartrackFuelFill { Litres = 83.16m, EventTs = "2026-09-20 14:41:54+02", IsAccurate = false }]
        });

        Assert.False(row.FuelReadingsAccurate);
    }

    // — The words on the fleet page —————————————————————————————————————

    private static FleetAuditVehicle Vehicle(Action<FleetAuditVehicle>? tune = null)
    {
        var vehicle = new FleetAuditVehicle { Registration = Plate, RegistrationNormalized = Plate };
        tune?.Invoke(vehicle);
        return vehicle;
    }

    [Fact]
    public void No_fuel_sensor_and_no_fuel_reported_are_different_sentences()
    {
        var fitting = FleetAuditCells.VehicleFuel(Vehicle());
        var fault = FleetAuditCells.VehicleFuel(Vehicle(v => v.HasAnyFuelSensor = true));

        Assert.Equal("no fuel sensor", fitting.Sub);
        Assert.Equal("fuel not reported", fault.Sub);
    }

    [Fact]
    public void A_provisional_fuel_figure_is_muted_and_never_flagged()
    {
        var cell = FleetAuditCells.VehicleFuel(Vehicle(v =>
        {
            v.HasAnyFuelSensor = true;
            v.DaysWithFuel = 1;
            v.FuelUsedLitres = 0.04m;
            v.FuelAllTrustworthy = false;
        }));

        Assert.True(cell.Muted);
        Assert.False(cell.Flagged);
        Assert.Equal("provisional", cell.Sub);
    }

    [Fact]
    public void A_van_with_no_probe_and_no_limits_is_not_a_finding()
    {
        var cell = FleetAuditCells.VehicleColdChain(Vehicle(v =>
        {
            v.DaysWithTemperature = 5;
            v.DaysWithReadings = 0;
            v.MinutesOutsideLimits = null;
        }));

        Assert.False(cell.Flagged);
        Assert.Equal("no probe", cell.Sub);
    }

    [Fact]
    public void Silence_on_a_round_with_limits_is_the_blind_spot_and_is_flagged()
    {
        // Somebody asked for this load to be kept cold, and nobody can say whether it was.
        var cell = FleetAuditCells.VehicleColdChain(Vehicle(v =>
        {
            v.DaysWithTemperature = 5;
            v.DaysWithReadings = 0;
            v.DaysMoved = 3;
            v.MinutesOutsideLimits = 0;
        }));

        Assert.True(cell.Flagged);
        Assert.Equal("no probe readings", cell.Sub);
    }

    [Fact]
    public void Silence_from_a_van_that_never_ran_is_not_the_blind_spot()
    {
        // Found live on the morning of 21 Sep: the day had limits and no readings because the
        // truck had not left yet. Flagging it would accuse an empty truck in the yard.
        var vehicle = FleetAuditCells.VehicleColdChain(Vehicle(v =>
        {
            v.DaysWithTemperature = 1;
            v.DaysWithReadings = 0;
            v.DaysMoved = 0;
            v.MinutesOutsideLimits = 0;
        }));

        var day = FleetAuditCells.DayColdChain(new FleetAuditDay
        {
            HasRollup = true,
            MovementRead = true,
            FirstIgnitionOn = null,
            Temperature = new FleetAuditTemperature { SampleCount = 0, LimitMinC = -18m, LimitMaxC = -12m }
        });

        Assert.False(vehicle.Flagged);
        Assert.False(day.Flagged);
    }

    [Fact]
    public void Silence_on_a_day_the_van_ran_with_limits_is_flagged()
    {
        var day = FleetAuditCells.DayColdChain(new FleetAuditDay
        {
            HasRollup = true,
            MovementRead = true,
            FirstIgnitionOn = new DateTime(2026, 9, 19, 7, 0, 0),
            Temperature = new FleetAuditTemperature { SampleCount = 0, LimitMinC = -18m, LimitMaxC = -12m }
        });

        Assert.True(day.Flagged);
        Assert.Equal("no readings", day.Sub);
    }

    [Fact]
    public void In_range_and_not_judged_do_not_read_alike()
    {
        var judged = FleetAuditCells.VehicleColdChain(Vehicle(v =>
        {
            v.DaysWithTemperature = 1;
            v.DaysWithReadings = 1;
            v.MinutesOutsideLimits = 0;
            v.TemperatureMinC = -16m;
            v.TemperatureMaxC = -13m;
        }));

        var unjudged = FleetAuditCells.VehicleColdChain(Vehicle(v =>
        {
            v.DaysWithTemperature = 1;
            v.DaysWithReadings = 1;
            v.MinutesOutsideLimits = null;
            v.TemperatureMinC = 10.1m;
            v.TemperatureMaxC = 17.8m;
        }));

        Assert.Equal("in range", judged.Figure);
        Assert.Equal("no limits set", unjudged.Sub);
        Assert.False(judged.Flagged);
        Assert.False(unjudged.Flagged);
    }

    [Fact]
    public void A_breach_is_flagged_with_its_duration()
    {
        var cell = FleetAuditCells.DayColdChain(new FleetAuditDay
        {
            Temperature = new FleetAuditTemperature
            {
                SampleCount = 28,
                MinC = 10.1m,
                MaxC = 17.8m,
                LimitMinC = -18m,
                LimitMaxC = -12m,
                MinutesAboveMax = 425,
                MinutesBelowMin = 0
            }
        });

        Assert.True(cell.Flagged);
        Assert.Equal("7h 05m outside -18 to -12 °C", cell.Sub);
    }

    // — Retention —————————————————————————————————————————————————————

    [Fact]
    public async Task Retention_removes_only_readings_older_than_the_cutoff_across_batches()
    {
        var cutoff = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < 5; i++)
        {
            _context.VehicleTemperatureSamples.Add(new VehicleTemperatureSampleEntity
            {
                RegistrationNormalized = Plate,
                TradingDate = new DateTime(2024, 12, 1),
                Channel = 1,
                EventAtUtc = cutoff.AddDays(-30).AddMinutes(i),
                TemperatureC = -15m
            });
        }

        _context.VehicleTemperatureSamples.Add(new VehicleTemperatureSampleEntity
        {
            RegistrationNormalized = Plate,
            TradingDate = new DateTime(2025, 1, 1),
            Channel = 1,
            EventAtUtc = cutoff.AddMinutes(1),
            TemperatureC = -15m
        });

        await _context.SaveChangesAsync();

        // A batch of two makes three passes over five rows, which is the loop this checks.
        var removed = await CartrackTemperatureRetentionJob.PurgeAsync(
            _context, cutoff, batchSize: 2, CancellationToken.None);

        Assert.Equal(5, removed);
        Assert.Equal(1, await _context.VehicleTemperatureSamples.CountAsync());
    }

    private sealed class StubClient : ICartrackClient
    {
        public IReadOnlyList<CartrackTemperatureReading> Temperatures { get; set; } = [];
        public Exception? TemperatureThrows { get; set; }
        public CartrackOdometerSummary? Odometer { get; set; }
        public CartrackFuelLevel? FuelLevel { get; set; }
        public IReadOnlyList<CartrackFuelFill> Fills { get; set; } = [];

        public int FuelCalls { get; private set; }
        public int ConsumedCalls { get; private set; }

        public Task<IReadOnlyList<CartrackVehicle>> GetVehiclesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CartrackVehicle>>([]);

        public Task<IReadOnlyList<CartrackVehicleActivity>> GetActivityAsync(
            DateTime tradingDate, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CartrackVehicleActivity>>([]);

        public Task<IReadOnlyList<CartrackVehicleEvent>> GetEventsAsync(
            DateTime fromUtc, DateTime toUtc, string? registration, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CartrackVehicleEvent>>([]);

        public DateTime? TemperatureAskedTo { get; private set; }

        public Task<IReadOnlyList<CartrackTemperatureReading>> GetTemperaturesAsync(
            DateTime fromUtc, DateTime toUtc, string? registration, CancellationToken cancellationToken)
        {
            TemperatureAskedTo = toUtc;

            return TemperatureThrows is not null
                ? Task.FromException<IReadOnlyList<CartrackTemperatureReading>>(TemperatureThrows)
                : Task.FromResult(Temperatures);
        }

        public Task<CartrackOdometerSummary?> GetOdometerAsync(
            string registration, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken) =>
            Task.FromResult(Odometer);

        public Task<CartrackFuelConsumed?> GetFuelConsumedAsync(
            string registration, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken)
        {
            FuelCalls++;
            ConsumedCalls++;
            return Task.FromResult<CartrackFuelConsumed?>(null);
        }

        public Task<CartrackFuelLevel?> GetFuelLevelAsync(
            string registration, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken)
        {
            FuelCalls++;
            return Task.FromResult(FuelLevel);
        }

        public Task<IReadOnlyList<CartrackFuelFill>> GetFuelFillsAsync(
            string registration, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken)
        {
            FuelCalls++;
            return Task.FromResult(Fills);
        }

        public Task<IReadOnlyList<CartrackVehicleStatus>> GetStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CartrackVehicleStatus>>([]);
    }
}
