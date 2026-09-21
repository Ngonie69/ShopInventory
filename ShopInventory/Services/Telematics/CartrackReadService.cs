using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Telematics;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Services.Telematics;

/// <summary>
/// Reads the telematics projection for a report.
/// </summary>
/// <remarks>
/// Deliberately separate from <see cref="ICartrackRollupService"/>, which builds it. A report
/// query has no business holding a handle that can call an external API and write rows, and
/// keeping them apart means it cannot start doing so by accident later.
/// </remarks>
public interface ICartrackReadService
{
    /// <summary>Whether the rollup can speak for this period, and why not when it cannot.</summary>
    Task<CartrackRollupStatus> GetStatusAsync(
        DateTime fromDate, DateTime toDate, CancellationToken cancellationToken);

    /// <summary>
    /// The day rollups for these vehicles over this period, keyed by normalised registration and
    /// trading date.
    /// </summary>
    Task<Dictionary<(string Registration, DateTime TradingDate), VehicleDayRollupEntity>>
        LoadRollupsAsync(
            IReadOnlyCollection<string> registrations,
            DateTime fromDate,
            DateTime toDate,
            CancellationToken cancellationToken);

    /// <summary>
    /// Which of these registrations the provider knows, and anything that would stop one
    /// reporting. A registration absent from the result is one the fleet does not hold.
    /// </summary>
    Task<Dictionary<string, TelematicsVehicleEntity>> LoadVehiclesAsync(
        IReadOnlyCollection<string> registrations, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ICartrackReadService"/>
public sealed class CartrackReadService(
    ApplicationDbContext db,
    IOptions<CartrackSettings> settings
) : ICartrackReadService
{
    private readonly CartrackSettings _settings = settings.Value;

    public async Task<CartrackRollupStatus> GetStatusAsync(
        DateTime fromDate, DateTime toDate, CancellationToken cancellationToken)
    {
        if (!_settings.Enabled)
        {
            return new CartrackRollupStatus(false, false, false, null, null, null,
                "Fleet telematics is switched off, so no vehicle data is available.");
        }

        if (!_settings.HasCredentials)
        {
            return new CartrackRollupStatus(true, false, false, null, null, null,
                "Fleet telematics is on but has no credentials, so no vehicle data is available.");
        }

        var lastSynced = await db.CacheSyncStates
            .AsNoTracking()
            .Where(state => state.CacheKey == CartrackRollupService.CacheKey)
            .Select(state => state.LastSyncedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var checkpoint = CartrackRollupCheckpointReader.TryRead(await db.SystemConfigs
            .AsNoTracking()
            .Where(config => config.Key == CartrackRollupService.CheckpointConfigKey)
            .Select(config => config.Value)
            .FirstOrDefaultAsync(cancellationToken));

        var from = checkpoint?.BackfillThroughDate;
        var through = checkpoint?.LastBuiltDate;

        if (lastSynced is null || checkpoint is null)
        {
            return new CartrackRollupStatus(true, true, false, lastSynced, from, through,
                "Fleet telematics has not run yet, so no vehicle data is available.");
        }

        var staleAfter = TimeSpan.FromHours(Math.Max(1, _settings.RollupReadyMaxAgeHours));

        if (DateTime.UtcNow - lastSynced.Value > staleAfter)
        {
            return new CartrackRollupStatus(true, true, false, lastSynced, from, through,
                $"Fleet telematics last ran {AuditService.ToCAT(lastSynced.Value):dd MMM HH:mm}, "
                + "so the vehicle figures below may be out of date.");
        }

        // A backfill that has reached September cannot speak for July. Rendering July as "the van
        // never moved" reads as a finding, which is worse than showing nothing at all.
        if (!checkpoint.BackfillCompleted || from is null || fromDate.Date < from.Value.Date)
        {
            var reached = from is { } f
                ? $"only reaches back to {f:d MMM yyyy}"
                : "has no history yet";

            return new CartrackRollupStatus(true, true, false, lastSynced, from, through,
                $"Fleet telematics {reached}, so it cannot speak for this whole period. "
                + "Narrow the dates to see the vehicle figures.");
        }

        if (through is null || toDate.Date > through.Value.Date)
        {
            return new CartrackRollupStatus(true, true, false, lastSynced, from, through,
                "Fleet telematics has not built the most recent day in this period yet.");
        }

        return new CartrackRollupStatus(true, true, true, lastSynced, from, through, null);
    }

    public async Task<Dictionary<(string, DateTime), VehicleDayRollupEntity>> LoadRollupsAsync(
        IReadOnlyCollection<string> registrations,
        DateTime fromDate,
        DateTime toDate,
        CancellationToken cancellationToken)
    {
        if (registrations.Count == 0)
        {
            return [];
        }

        var keys = registrations.ToList();

        var rows = await db.VehicleDayRollups
            .AsNoTracking()
            .Where(rollup => keys.Contains(rollup.RegistrationNormalized)
                             && rollup.TradingDate >= fromDate.Date
                             && rollup.TradingDate <= toDate.Date)
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(row => (row.RegistrationNormalized, row.TradingDate));
    }

    public async Task<Dictionary<string, TelematicsVehicleEntity>> LoadVehiclesAsync(
        IReadOnlyCollection<string> registrations, CancellationToken cancellationToken)
    {
        if (registrations.Count == 0)
        {
            return new Dictionary<string, TelematicsVehicleEntity>(TelematicsRegistration.Comparer);
        }

        var keys = registrations.ToList();

        var rows = await db.TelematicsVehicles
            .AsNoTracking()
            .Where(vehicle => keys.Contains(vehicle.RegistrationNormalized))
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(
            vehicle => vehicle.RegistrationNormalized, TelematicsRegistration.Comparer);
    }
}
