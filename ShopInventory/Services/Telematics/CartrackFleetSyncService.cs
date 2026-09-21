using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Caching;
using ShopInventory.Common.Telematics;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Services.Telematics;

/// <summary>Refreshes the cached fleet from the telematics provider.</summary>
public interface ICartrackFleetSyncService
{
    /// <summary>
    /// Reads the provider's vehicle list and brings <c>TelematicsVehicles</c> into line with it.
    /// Returns how many vehicles are now active in the fleet, or null when the sync did not run.
    /// </summary>
    Task<int?> SyncAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="ICartrackFleetSyncService"/>
/// <remarks>
/// <para>
/// A small, complete sync rather than an incremental one: the fleet is a handful of vehicles and
/// one request returns all of them, so there is no cursor, no checkpoint and nothing to get
/// stuck. What it does have to be careful about is <b>not clobbering the two columns it does not
/// own</b> — whether a temperature probe has ever reported, and when. The provider publishes no
/// temperature capability at all, so those are learned from readings arriving and would be lost
/// on every sync if this wrote the whole row.
/// </para>
/// <para>
/// A vehicle that disappears from the provider's list is retired, never deleted. Historic rollups
/// and temperature samples key on the registration, and deleting the vehicle would turn last
/// month's rows from "this truck ran the round" into "not in the fleet".
/// </para>
/// </remarks>
public sealed class CartrackFleetSyncService(
    ApplicationDbContext db,
    ICartrackClient client,
    IOptions<CartrackSettings> settings,
    CacheSyncStateRecorder syncState,
    ILogger<CartrackFleetSyncService> logger
) : ICartrackFleetSyncService
{
    /// <summary>The key this sync reports under on the sync status page.</summary>
    public const string CacheKey = "CartrackFleet";

    private const string DisplayName = "Fleet telematics vehicles";

    private readonly CartrackSettings _settings = settings.Value;

    public async Task<int?> SyncAsync(CancellationToken cancellationToken)
    {
        if (!_settings.Enabled || !_settings.HasCredentials)
        {
            return null;
        }

        var startedAt = DateTime.UtcNow;

        try
        {
            var fleet = await client.GetVehiclesAsync(cancellationToken);
            var existing = await db.TelematicsVehicles.ToListAsync(cancellationToken);

            var byRegistration = existing.ToDictionary(
                vehicle => vehicle.RegistrationNormalized, TelematicsRegistration.Comparer);

            var seen = new HashSet<string>(TelematicsRegistration.Comparer);
            var added = 0;

            foreach (var vehicle in fleet)
            {
                var key = TelematicsRegistration.Normalize(vehicle.Registration);

                if (key is null)
                {
                    // A vehicle with no plate cannot be joined to a route, so it is not a vehicle
                    // this system can say anything about. Counted, not stored.
                    logger.LogWarning(
                        "Cartrack returned a vehicle with no registration (vehicle_id {VehicleId}). "
                        + "It cannot be matched to a route and has been skipped.",
                        vehicle.VehicleId);

                    continue;
                }

                // The provider's list can hold two rows for one plate — a replaced tracker, most
                // often. First wins, so the sync is stable rather than depending on list order.
                if (!seen.Add(key))
                {
                    continue;
                }

                if (!byRegistration.TryGetValue(key, out var row))
                {
                    row = new TelematicsVehicleEntity
                    {
                        RegistrationNormalized = key,
                        FirstSeenAtUtc = startedAt
                    };

                    db.TelematicsVehicles.Add(row);
                    added++;
                }

                // Provider-owned columns only. Four are deliberately absent and must stay so:
                // HasTemperatureProbe and LastTemperatureSeenAtUtc are inferred from readings,
                // and BusinessPartnerCode with BusinessPartnerName are this company's own
                // mapping of truck to van account. Writing the whole row would erase all four
                // every night, and the provider could not supply any of them.
                row.Registration = vehicle.Registration?.Trim();
                row.CartrackVehicleId = vehicle.VehicleId;
                row.TerminalSerial = vehicle.TerminalSerial?.Trim();
                row.ClientVehicleName = vehicle.ClientVehicleName?.Trim();
                row.Manufacturer = vehicle.Manufacturer?.Trim();
                row.Model = vehicle.Model?.Trim();
                row.IsUnderMaintenance = vehicle.IsUnderMaintenance ?? false;
                row.TerminalInRepair = vehicle.TerminalInRepair ?? false;
                row.HasFuelCanbusConsumed = vehicle.Sensors?.FuelCanbusConsumed ?? false;
                row.HasFuelCanbusLevel = vehicle.Sensors?.FuelCanbusLevel ?? false;
                row.HasFuelAnalogLevel = vehicle.Sensors?.FuelAnalogLevel ?? false;
                row.IsActiveInFleet = true;
                row.LastSeenAtUtc = startedAt;
            }

            var retired = 0;

            foreach (var row in existing.Where(row => !seen.Contains(row.RegistrationNormalized)))
            {
                if (row.IsActiveInFleet)
                {
                    row.IsActiveInFleet = false;
                    retired++;
                }
            }

            await db.SaveChangesAsync(cancellationToken);

            // Logged only when something moved. A nightly job that reports the same four vehicles
            // every night is how a real change stops being noticed.
            if (added > 0 || retired > 0)
            {
                logger.LogInformation(
                    "Cartrack fleet synced: {Added} added, {Retired} retired, {Active} active.",
                    added, retired, seen.Count);
            }

            await syncState.RecordSuccessAsync(
                CacheKey, DisplayName, seen.Count, DateTime.UtcNow, cancellationToken);

            return seen.Count;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Cartrack fleet sync failed");

            await syncState.RecordFailureAsync(
                CacheKey, DisplayName, ex.Message, DateTime.UtcNow, CancellationToken.None);

            throw;
        }
    }
}
