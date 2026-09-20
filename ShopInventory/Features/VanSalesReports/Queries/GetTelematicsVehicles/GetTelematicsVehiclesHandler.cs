using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Services.Telematics;

namespace ShopInventory.Features.VanSalesReports.Queries.GetTelematicsVehicles;

public sealed class GetTelematicsVehiclesHandler(
    ApplicationDbContext db,
    IOptions<CartrackSettings> settings
) : IRequestHandler<GetTelematicsVehiclesQuery, ErrorOr<TelematicsVehiclesResult>>
{
    private readonly CartrackSettings _settings = settings.Value;

    public async Task<ErrorOr<TelematicsVehiclesResult>> Handle(
        GetTelematicsVehiclesQuery query,
        CancellationToken cancellationToken)
    {
        var queryable = db.TelematicsVehicles.AsNoTracking();

        if (!query.IncludeRetired)
        {
            queryable = queryable.Where(vehicle => vehicle.IsActiveInFleet);
        }

        var vehicles = await queryable
            .OrderBy(vehicle => vehicle.RegistrationNormalized)
            .Select(vehicle => new TelematicsVehicleDto(
                vehicle.Registration ?? vehicle.RegistrationNormalized,
                vehicle.RegistrationNormalized,
                vehicle.ClientVehicleName,
                // Make and model together, or whichever of them there is. A picker showing eight
                // plates is easier to use when one of them says "Mercedes-Benz Axor".
                vehicle.Manufacturer == null
                    ? vehicle.Model
                    : vehicle.Model == null
                        ? vehicle.Manufacturer
                        : vehicle.Manufacturer + " " + vehicle.Model,
                vehicle.HasFuelCanbusConsumed || vehicle.HasFuelCanbusLevel || vehicle.HasFuelAnalogLevel,
                vehicle.HasTemperatureProbe,
                vehicle.IsActiveInFleet,
                !vehicle.IsActiveInFleet
                    ? "no longer in the fleet"
                    : vehicle.TerminalInRepair
                        ? "tracker in repair"
                        : vehicle.IsUnderMaintenance
                            ? "under maintenance"
                            : null))
            .ToListAsync(cancellationToken);

        var lastSynced = await db.CacheSyncStates
            .AsNoTracking()
            .Where(state => state.CacheKey == CartrackFleetSyncService.CacheKey)
            .Select(state => (DateTime?)state.LastSyncedAt)
            .FirstOrDefaultAsync(cancellationToken);

        return new TelematicsVehiclesResult(
            _settings.Enabled,
            _settings.HasCredentials,
            lastSynced,
            ReasonFor(vehicles.Count, lastSynced),
            vehicles);
    }

    /// <summary>
    /// Why the list is empty, in words the page can print. Null when there is nothing to explain.
    /// </summary>
    private string? ReasonFor(int count, DateTime? lastSynced)
    {
        if (!_settings.Enabled)
        {
            return "Fleet telematics is switched off, so no vehicle list is available. "
                   + "Type the registration instead.";
        }

        if (!_settings.HasCredentials)
        {
            return "Fleet telematics is on but has no credentials, so the vehicle list is empty. "
                   + "An administrator generates them in Fleetweb under API Settings.";
        }

        if (lastSynced is null)
        {
            return "The vehicle list has not been fetched yet. It is refreshed a few minutes "
                   + "after the service starts, and nightly after that.";
        }

        return count == 0
            ? "The fleet came back empty. Check that this Cartrack account holds the vehicles."
            : null;
    }
}
