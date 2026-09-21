using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Features.VanSalesReports.Commands.LinkVehicleBusinessPartner;
using ShopInventory.Features.VanSalesReports.Queries.GetFleetAudit;
using ShopInventory.Models.Entities;
using ShopInventory.Services;
using ShopInventory.Services.Telematics;
using Xunit;

namespace ShopInventory.Tests;

/// <summary>
/// The fleet audit, and linking a truck to the van account whose takings it carries.
/// </summary>
/// <remarks>
/// Two things here are worth more than the arithmetic. A vehicle with no account linked must
/// show <b>no</b> money rather than zero — a van nobody has mapped has not sold nothing — and a
/// truck must not be linkable to an ordinary customer, because that would report the customer's
/// whole trade as this van's takings and the figure would look entirely plausible.
/// </remarks>
public class FleetAuditTests : IDisposable
{
    private const string Plate = "AFQ9644";
    private static readonly DateTime From = new(2026, 9, 18);
    private static readonly DateTime To = new(2026, 9, 19);

    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public FleetAuditTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _context = new ApplicationDbContext(
            new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_connection).Options);

        _context.Database.EnsureCreated();

        _context.TelematicsVehicles.Add(new TelematicsVehicleEntity
        {
            RegistrationNormalized = Plate,
            Registration = Plate,
            ClientVehicleName = "306_AFQ9644",
            Manufacturer = "Mercedes-Benz",
            Model = "Axor",
            IsActiveInFleet = true
        });

        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private static CartrackSettings Settings() => new()
    {
        Enabled = true,
        Username = "u",
        Password = "p",
        RollupReadyMaxAgeHours = 36
    };

    private GetFleetAuditHandler Handler() =>
        new(_context, new CartrackReadService(_context, Options.Create(Settings())));

    private LinkVehicleBusinessPartnerHandler Linker() =>
        new(_context,
            new NoOpAuditService(),
            NullLogger<LinkVehicleBusinessPartnerHandler>.Instance);

    private void AddRollup(DateTime date, int distanceKm, bool moved = true)
    {
        _context.VehicleDayRollups.Add(new VehicleDayRollupEntity
        {
            RegistrationNormalized = Plate,
            TradingDate = date,
            HasActivity = true,
            HasOdometer = true,
            FirstIgnitionOnUtc = AuditService.FromCAT(date.AddHours(6)),
            FirstDepartureUtc = moved ? AuditService.FromCAT(date.AddHours(7)) : null,
            DistanceMetres = distanceKm * 1000L,
            DrivingSeconds = 3600,
            IdleSeconds = 1800
        });

        _context.SaveChanges();
    }

    private async Task<FleetAuditVehicleDto> RunAsync()
    {
        var result = await Handler().Handle(
            new GetFleetAuditQuery(From, To), CancellationToken.None);

        Assert.False(result.IsError);

        return Assert.Single(result.Value.Vehicles);
    }

    // — Every day is listed ————————————————————————————————————————————

    [Fact]
    public async Task A_truck_that_reported_nothing_shows_a_run_of_silent_days()
    {
        // Not an empty list. Silence over a fortnight is the finding, and a short list reads as
        // a short period instead.
        var vehicle = await RunAsync();

        Assert.Equal(2, vehicle.Days.Count);
        Assert.Equal(0, vehicle.DaysWithData);
        Assert.Equal(2, vehicle.DaysSilent);
        Assert.True(vehicle.NeverReported);
    }

    [Fact]
    public async Task Days_that_reported_are_counted_apart_from_days_that_did_not()
    {
        AddRollup(From, distanceKm: 120);

        var vehicle = await RunAsync();

        Assert.Equal(1, vehicle.DaysWithData);
        Assert.Equal(1, vehicle.DaysSilent);
        Assert.Equal(1, vehicle.DaysMoved);
        Assert.Equal(120, vehicle.TotalDistanceKm);
        Assert.False(vehicle.NeverReported);
    }

    [Fact]
    public async Task The_average_is_over_the_days_it_moved_not_the_whole_period()
    {
        // Otherwise a fortnight of silence drags the average down as though the truck had been
        // standing still, which is a different claim from "it did not report".
        AddRollup(From, distanceKm: 100);

        var vehicle = await RunAsync();

        Assert.Equal(100, vehicle.AverageDistanceKm);
    }

    [Fact]
    public async Task A_van_that_started_and_never_left_is_its_own_state()
    {
        AddRollup(From, distanceKm: 0, moved: false);

        var vehicle = await RunAsync();
        var day = vehicle.Days.Single(row => row.TradingDate == From);

        Assert.True(day.DidNotMove);
        Assert.Equal(0, vehicle.DaysMoved);
        Assert.Equal(1, vehicle.DaysWithData);
    }

    // — Fuel and cold chain ——————————————————————————————————————————

    [Fact]
    public async Task A_fuel_day_with_nothing_in_it_does_not_taint_the_period()
    {
        // Today before the van has run: read successfully, no figures. The settled day beside it
        // is the only figure in the total, so the total is as trustworthy as that day.
        AddRollup(From, distanceKm: 100);
        AddRollup(To, distanceKm: 0, moved: false);

        foreach (var row in _context.VehicleDayRollups)
        {
            row.HasFuel = true;

            if (row.TradingDate == From)
            {
                row.FuelLevelStartLitres = 71.11m;
                row.FuelLevelEndLitres = 166.33m;
                row.EstimatedFuelUsedLitres = 27.58m;
                row.FuelIsCalibrated = true;
                row.FuelReadingsAccurate = true;
            }
        }

        await _context.SaveChangesAsync();

        var vehicle = await RunAsync();

        Assert.Equal(1, vehicle.DaysWithFuel);
        Assert.Equal(27.58m, vehicle.FuelUsedLitres);
        Assert.True(vehicle.FuelAllTrustworthy);
    }

    [Fact]
    public async Task Breach_minutes_total_only_the_judged_days()
    {
        AddRollup(From, distanceKm: 100);
        AddRollup(To, distanceKm: 100);

        foreach (var row in _context.VehicleDayRollups)
        {
            row.HasTemperature = true;
            row.TemperatureSampleCount = 20;
            row.TemperatureMinC = -15m;
            row.TemperatureMaxC = row.TradingDate == From ? 4m : -13m;

            if (row.TradingDate == From)
            {
                row.LimitMinC = -18m;
                row.LimitMaxC = -12m;
                row.MinutesAboveMaxLimit = 45;
                row.MinutesBelowMinLimit = 0;
            }
        }

        await _context.SaveChangesAsync();

        var vehicle = await RunAsync();

        Assert.Equal(2, vehicle.DaysWithReadings);
        Assert.Equal(1, vehicle.DaysBreached);
        Assert.Equal(45, vehicle.MinutesOutsideLimits);
        Assert.Equal(4m, vehicle.TemperatureMaxC);
    }

    // — Money is only shown where it can be attributed ————————————————

    [Fact]
    public async Task A_vehicle_with_no_account_has_no_money_at_all()
    {
        AddRollup(From, distanceKm: 100);

        var vehicle = await RunAsync();

        Assert.Null(vehicle.BusinessPartnerCode);
        Assert.Null(vehicle.SalesPerKm);
        Assert.Null(vehicle.KmPerSale);
        Assert.All(vehicle.Days, day => Assert.Null(day.Sales));
    }

    [Fact]
    public async Task A_linked_vehicle_that_sold_nothing_shows_zero_rather_than_nothing()
    {
        // The other side of the rule. Once an account is linked, zero is a fact about the van
        // and has to be distinguishable from "nobody can say".
        AddRollup(From, distanceKm: 100);
        await Linker().Handle(
            new LinkVehicleBusinessPartnerCommand(Plate, "VAN010", "Van Sales CBD"),
            CancellationToken.None);

        var vehicle = await RunAsync();

        Assert.Equal("VAN010", vehicle.BusinessPartnerCode);
        Assert.Equal(0m, vehicle.TotalSales);
        Assert.All(vehicle.Days, day => Assert.NotNull(day.Sales));
    }

    // — Linking ————————————————————————————————————————————————————————

    [Fact]
    public async Task A_truck_cannot_be_linked_to_an_ordinary_customer()
    {
        // The guard that matters. An ordinary customer's trade reported as one van's takings is
        // a wrong figure that looks completely plausible.
        var result = await Linker().Handle(
            new LinkVehicleBusinessPartnerCommand(Plate, "ABB001", "Abbiamo Trading"),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("Fleet.NotAVanAccount", result.FirstError.Code);
        Assert.Null(_context.TelematicsVehicles.Single().BusinessPartnerCode);
    }

    [Fact]
    public async Task One_account_cannot_be_linked_to_two_trucks()
    {
        // Both would report the account's full takings, so the same money would be counted
        // twice on one page.
        _context.TelematicsVehicles.Add(new TelematicsVehicleEntity
        {
            RegistrationNormalized = "ACQ3455",
            Registration = "ACQ3455",
            IsActiveInFleet = true
        });

        await _context.SaveChangesAsync();

        await Linker().Handle(
            new LinkVehicleBusinessPartnerCommand(Plate, "VAN010", "Van Sales CBD"),
            CancellationToken.None);

        var second = await Linker().Handle(
            new LinkVehicleBusinessPartnerCommand("ACQ3455", "VAN010", "Van Sales CBD"),
            CancellationToken.None);

        Assert.True(second.IsError);
        Assert.Equal("Fleet.AccountAlreadyLinked", second.FirstError.Code);
    }

    [Fact]
    public async Task Relinking_the_same_truck_to_the_same_account_is_allowed()
    {
        // The duplicate guard must not refuse a vehicle its own account, or saving the form
        // twice becomes an error.
        await Linker().Handle(
            new LinkVehicleBusinessPartnerCommand(Plate, "VAN010", "Van Sales CBD"),
            CancellationToken.None);

        var again = await Linker().Handle(
            new LinkVehicleBusinessPartnerCommand(Plate, "VAN010", "Van Sales CBD"),
            CancellationToken.None);

        Assert.False(again.IsError);
    }

    [Fact]
    public async Task An_empty_code_unlinks()
    {
        await Linker().Handle(
            new LinkVehicleBusinessPartnerCommand(Plate, "VAN010", "Van Sales CBD"),
            CancellationToken.None);

        var cleared = await Linker().Handle(
            new LinkVehicleBusinessPartnerCommand(Plate, "  ", null),
            CancellationToken.None);

        Assert.False(cleared.IsError);

        var row = _context.TelematicsVehicles.Single();
        Assert.Null(row.BusinessPartnerCode);
        Assert.Null(row.BusinessPartnerName);
    }

    [Fact]
    public async Task A_registration_the_fleet_does_not_hold_is_a_not_found()
    {
        var result = await Linker().Handle(
            new LinkVehicleBusinessPartnerCommand("BAD1234", "VAN010", null),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("Fleet.VehicleNotFound", result.FirstError.Code);
    }

    [Fact]
    public async Task A_registration_is_matched_however_it_is_spelled()
    {
        var result = await Linker().Handle(
            new LinkVehicleBusinessPartnerCommand("afq-9644", "VAN010", "Van Sales CBD"),
            CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal("VAN010", _context.TelematicsVehicles.Single().BusinessPartnerCode);
    }

    // — The range guard ————————————————————————————————————————————————

    [Fact]
    public async Task An_inverted_range_is_refused()
    {
        var result = await Handler().Handle(
            new GetFleetAuditQuery(To, From), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("FleetAudit.InvalidRange", result.FirstError.Code);
    }

    [Fact]
    public async Task A_range_past_the_ceiling_is_refused()
    {
        // A day per vehicle means the row count is the period times the fleet, so an unbounded
        // range is a slow page rather than an error anybody notices.
        var result = await Handler().Handle(
            new GetFleetAuditQuery(From.AddYears(-3), To), CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("FleetAudit.RangeTooWide", result.FirstError.Code);
    }

    /// <summary>The audit trail is not what these tests are about, and it must not fail them.</summary>
    private sealed class NoOpAuditService : IAuditService
    {
        public Task LogAsync(string action, string username, string userRole,
            string? entityType = null, string? entityId = null, string? details = null,
            string? endpoint = null, bool isSuccess = true, string? errorMessage = null) =>
            Task.CompletedTask;

        public Task LogAsync(string action, string? entityType = null, string? entityId = null) =>
            Task.CompletedTask;

        public Task LogAsync(string action, string? entityType, string? entityId, string? details,
            bool isSuccess, string? errorMessage = null) =>
            Task.CompletedTask;
    }
}
