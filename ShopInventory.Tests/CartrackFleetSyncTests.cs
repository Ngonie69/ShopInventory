using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Caching;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;
using ShopInventory.Models.Telematics;
using ShopInventory.Services.Telematics;
using Xunit;

namespace ShopInventory.Tests;

/// <summary>
/// Bringing the cached fleet into line with the provider's list.
/// </summary>
/// <remarks>
/// Two of these guard data that cannot be recovered if the sync gets it wrong. Whether a
/// temperature probe has ever reported is <em>inferred</em> — the provider publishes no
/// capability for it — so a sync that wrote the whole row would erase the only record of it every
/// night. And a vehicle that leaves the fleet must be retired rather than deleted, or last
/// month's rollups stop having a truck.
/// </remarks>
public class CartrackFleetSyncTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ApplicationDbContext _context;

    public CartrackFleetSyncTests()
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
        GC.SuppressFinalize(this);
    }

    private CartrackFleetSyncService Service(
        IReadOnlyList<CartrackVehicle> fleet, bool enabled = true, bool credentialed = true)
    {
        var settings = new CartrackSettings
        {
            Enabled = enabled,
            Username = credentialed ? "u" : string.Empty,
            Password = credentialed ? "p" : string.Empty
        };

        return new CartrackFleetSyncService(
            _context,
            new StubClient(fleet),
            Options.Create(settings),
            new CacheSyncStateRecorder(
                new SingleContextScopeFactory(_context), NullLogger<CacheSyncStateRecorder>.Instance),
            NullLogger<CartrackFleetSyncService>.Instance);
    }

    private static CartrackVehicle Vehicle(string registration, bool analogFuel = false) => new()
    {
        Registration = registration,
        VehicleId = registration.GetHashCode(),
        ClientVehicleName = "306_" + registration,
        Manufacturer = "Mercedes-Benz",
        Model = "Axor",
        Sensors = new CartrackVehicleSensors { FuelAnalogLevel = analogFuel }
    };

    [Fact]
    public async Task A_first_sync_stores_the_fleet_under_a_normalised_registration()
    {
        var count = await Service([Vehicle("AFQ 9644", analogFuel: true)])
            .SyncAsync(CancellationToken.None);

        Assert.Equal(1, count);

        var stored = Assert.Single(_context.TelematicsVehicles);

        // Keyed on the normalised form; the plate is kept as the provider spells it.
        Assert.Equal("AFQ9644", stored.RegistrationNormalized);
        Assert.Equal("AFQ 9644", stored.Registration);
        Assert.True(stored.HasFuelAnalogLevel);
        Assert.True(stored.HasAnyFuelSensor);
        Assert.True(stored.IsActiveInFleet);
    }

    [Fact]
    public async Task A_second_sync_updates_rather_than_duplicates()
    {
        await Service([Vehicle("AFQ9644")]).SyncAsync(CancellationToken.None);

        var changed = Vehicle("AFQ9644", analogFuel: true);
        changed.Model = "Actros";

        await Service([changed]).SyncAsync(CancellationToken.None);

        var stored = Assert.Single(_context.TelematicsVehicles);
        Assert.Equal("Actros", stored.Model);
        Assert.True(stored.HasFuelAnalogLevel);
    }

    [Fact]
    public async Task A_sync_never_erases_what_it_learned_about_a_temperature_probe()
    {
        // The whole reason the upsert is written column by column. The provider publishes no
        // temperature capability at all, so this is inferred from readings arriving — and a sync
        // that wrote the whole row would throw it away every night.
        await Service([Vehicle("AFQ9644")]).SyncAsync(CancellationToken.None);

        var seenAt = new DateTime(2026, 9, 19, 17, 37, 7, DateTimeKind.Utc);
        var row = _context.TelematicsVehicles.Single();
        row.HasTemperatureProbe = true;
        row.LastTemperatureSeenAtUtc = seenAt;
        await _context.SaveChangesAsync();

        await Service([Vehicle("AFQ9644")]).SyncAsync(CancellationToken.None);

        var after = _context.TelematicsVehicles.Single();
        Assert.True(after.HasTemperatureProbe);
        Assert.Equal(seenAt, after.LastTemperatureSeenAtUtc);
    }

    [Fact]
    public async Task A_sync_never_erases_the_van_account_a_truck_was_linked_to()
    {
        // The same rule as the temperature columns, for the same reason: the provider knows
        // nothing about this company's selling accounts, so a sync that wrote the whole row
        // would unlink every truck from its van every night.
        await Service([Vehicle("AFQ9644")]).SyncAsync(CancellationToken.None);

        var row = _context.TelematicsVehicles.Single();
        row.BusinessPartnerCode = "VAN010";
        row.BusinessPartnerName = "Van 010 — Harare North";
        await _context.SaveChangesAsync();

        await Service([Vehicle("AFQ9644")]).SyncAsync(CancellationToken.None);

        var after = _context.TelematicsVehicles.Single();
        Assert.Equal("VAN010", after.BusinessPartnerCode);
        Assert.Equal("Van 010 — Harare North", after.BusinessPartnerName);
    }

    [Fact]
    public async Task A_vehicle_that_leaves_the_fleet_is_retired_not_deleted()
    {
        await Service([Vehicle("AFQ9644"), Vehicle("ACQ3455")]).SyncAsync(CancellationToken.None);

        await Service([Vehicle("AFQ9644")]).SyncAsync(CancellationToken.None);

        // Still two rows: a rollup from last month keys on ACQ3455 and must keep naming a truck.
        Assert.Equal(2, _context.TelematicsVehicles.Count());

        var gone = _context.TelematicsVehicles.Single(v => v.RegistrationNormalized == "ACQ3455");
        Assert.False(gone.IsActiveInFleet);
    }

    [Fact]
    public async Task A_vehicle_that_comes_back_is_active_again()
    {
        await Service([Vehicle("ACQ3455")]).SyncAsync(CancellationToken.None);
        await Service([]).SyncAsync(CancellationToken.None);
        await Service([Vehicle("ACQ3455")]).SyncAsync(CancellationToken.None);

        Assert.True(_context.TelematicsVehicles.Single().IsActiveInFleet);
    }

    [Fact]
    public async Task A_vehicle_with_no_registration_is_skipped_rather_than_stored()
    {
        // It cannot be matched to a route, so there is nothing this system could say about it.
        var nameless = new CartrackVehicle { VehicleId = 1, Registration = "  " };

        var count = await Service([nameless, Vehicle("AFQ9644")]).SyncAsync(CancellationToken.None);

        Assert.Equal(1, count);
        Assert.Single(_context.TelematicsVehicles);
    }

    [Fact]
    public async Task Two_rows_for_one_plate_collapse_to_one_vehicle()
    {
        // A replaced tracker can leave the provider listing a plate twice. First wins, so the
        // result does not depend on list order.
        var count = await Service([Vehicle("AFQ9644"), Vehicle("afq-9644")])
            .SyncAsync(CancellationToken.None);

        Assert.Equal(1, count);
        Assert.Single(_context.TelematicsVehicles);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Nothing_happens_when_the_integration_is_off_or_uncredentialed(
        bool enabled, bool credentialed)
    {
        var count = await Service([Vehicle("AFQ9644")], enabled, credentialed)
            .SyncAsync(CancellationToken.None);

        Assert.Null(count);
        Assert.Empty(_context.TelematicsVehicles);
    }

    [Fact]
    public async Task A_sync_records_itself_so_the_status_page_can_show_it()
    {
        await Service([Vehicle("AFQ9644")]).SyncAsync(CancellationToken.None);

        var state = Assert.Single(
            _context.CacheSyncStates.Where(s => s.CacheKey == CartrackFleetSyncService.CacheKey));

        Assert.Equal(1, state.ItemCount);
        Assert.NotNull(state.LastSyncedAt);
        Assert.Null(state.LastError);
    }

    private sealed class StubClient(IReadOnlyList<CartrackVehicle> fleet) : ICartrackClient
    {
        public Task<IReadOnlyList<CartrackVehicle>> GetVehiclesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(fleet);

        public Task<IReadOnlyList<CartrackVehicleActivity>> GetActivityAsync(
            DateTime tradingDate, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CartrackVehicleActivity>>([]);

        public Task<IReadOnlyList<CartrackVehicleEvent>> GetEventsAsync(
            DateTime fromUtc, DateTime toUtc, string? registration, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CartrackVehicleEvent>>([]);

        public Task<IReadOnlyList<CartrackTemperatureReading>> GetTemperaturesAsync(
            DateTime fromUtc, DateTime toUtc, string? registration, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CartrackTemperatureReading>>([]);

        public Task<CartrackOdometerSummary?> GetOdometerAsync(
            string registration, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken) =>
            Task.FromResult<CartrackOdometerSummary?>(null);

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

    /// <summary>Hands the recorder the same context the service is using, so both see one database.</summary>
    private sealed class SingleContextScopeFactory(ApplicationDbContext context) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(context);

        private sealed class Scope(ApplicationDbContext context) : IServiceScope, IServiceProvider
        {
            public IServiceProvider ServiceProvider => this;

            public object? GetService(Type serviceType) =>
                serviceType == typeof(ApplicationDbContext) ? context : null;

            public void Dispose()
            {
            }
        }
    }
}
