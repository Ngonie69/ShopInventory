using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Telematics;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;
using ShopInventory.Models.Telematics;

namespace ShopInventory.Services.Telematics;

/// <summary>Builds the per-vehicle, per-day telematics rollup the compliance report joins to.</summary>
public interface ICartrackRollupService
{
    /// <summary>
    /// Runs one pass: a slice of backfill if there is any left, then yesterday and today, then a
    /// reconciliation window once a calendar day. Returns how many day rows were written.
    /// </summary>
    Task<int> SyncAsync(CancellationToken cancellationToken);

    /// <summary>Rebuilds exactly one vehicle-day, for a retry or a test.</summary>
    Task<bool> BuildDayAsync(string registration, DateTime tradingDate, CancellationToken cancellationToken);
}

/// <summary>
/// Turns the fleet provider's raw events into one row per vehicle per trading day.
/// </summary>
/// <remarks>
/// <para>
/// Modelled on <see cref="CreditNoteProjectionSyncService"/>: a sync-state row so the status page
/// can show it, a resumable checkpoint so a cold start spreads over nights, an incremental pass
/// that overlaps onto idempotent upserts, and a daily reconciliation because late data is normal.
/// </para>
/// <para>
/// <b>Ignition-on is not departure.</b> A driver warming a diesel, a fridge pulling the box down
/// before loading, a mechanic turning a key — all of them fire <c>IGNITION_ON</c> in the yard. So
/// both are stored: <see cref="VehicleDayRollupEntity.FirstIgnitionOnUtc"/> is when the key
/// turned, and <see cref="VehicleDayRollupEntity.FirstDepartureUtc"/> is when the vehicle first
/// got further than <see cref="CartrackSettings.DepotRadiusMetres"/> from where the day's
/// departure was recorded. Which one grades the day is a setting, not a decision made here.
/// </para>
/// <para>
/// <b>Each facet fails on its own.</b> A day can be built while one endpoint is down, so every
/// facet writes its own columns and its own <c>Has…</c> flag and the checkpoint only moves past a
/// date once they all reported. Without that the report cannot tell "we did not ask" from "we
/// asked and the van said nothing", and those mean different things to a supervisor.
/// </para>
/// </remarks>
public sealed class CartrackRollupService(
    ApplicationDbContext db,
    ICartrackClient client,
    IOptions<CartrackSettings> settings,
    ILogger<CartrackRollupService> logger
) : ICartrackRollupService
{
    public const string CacheKey = "CartrackRollup";

    /// <summary>The SystemConfigs key the checkpoint is stored under. Read by the read service too.</summary>
    public const string CheckpointConfigKey = "Cartrack.Rollup.Checkpoint";
    private const string DisplayName = "Fleet telematics rollup";
    private const int MaxErrorLength = 1000;

    private readonly CartrackSettings _settings = settings.Value;

    public async Task<int> SyncAsync(CancellationToken cancellationToken)
    {
        if (!_settings.Enabled || !_settings.HasCredentials)
        {
            return 0;
        }

        var now = DateTime.UtcNow;
        var today = CartrackTime.TradingDateOf(now);

        var syncState = await GetOrCreateSyncStateAsync(cancellationToken);
        syncState.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            var (row, checkpoint) = await GetCheckpointAsync(now, today, cancellationToken);
            var built = 0;

            if (!checkpoint.BackfillCompleted)
            {
                (checkpoint, var backfilled) =
                    await RunBackfillAsync(checkpoint, today, now, cancellationToken);

                built += backfilled;
                await SaveCheckpointAsync(row, checkpoint, now, cancellationToken);
            }

            // Yesterday and today, every pass. Yesterday because a van reporting from a dead spot
            // arrives late; today because the hourly trigger is what keeps the live-ish figures
            // on the report moving.
            foreach (var date in new[] { today.AddDays(-1), today })
            {
                built += (await BuildDateAsync(date, cancellationToken)).Rows;
            }

            checkpoint = checkpoint with { LastBuiltDate = today };

            // Gated on the CAT trading date, not on the UTC one. Everything else in this service
            // thinks in trading days, and a UTC gate would roll the "once a day" boundary at
            // 02:00 CAT — inside the night the nightly pass runs in.
            var reconciledOn = checkpoint.LastReconciledAtUtc is { } last
                ? CartrackTime.TradingDateOf(last)
                : (DateTime?)null;

            if (reconciledOn is null || reconciledOn < today)
            {
                var window = Math.Max(1, _settings.ReconciliationWindowDays);
                var reconciled = true;

                for (var back = 2; back <= window; back++)
                {
                    var pass = await BuildDateAsync(today.AddDays(-back), cancellationToken);

                    built += pass.Rows;
                    reconciled &= pass.MovementLoaded;
                }

                // Only mark it done if it actually reconciled. A pass that could not reach the
                // provider has re-read nothing, and marking it done would cost a whole day before
                // anything tried again — which is a long time to leave a corrected figure unread.
                if (reconciled)
                {
                    checkpoint = checkpoint with { LastReconciledAtUtc = now };
                }
            }

            await SaveCheckpointAsync(row, checkpoint, now, cancellationToken);

            var previous = syncState.ItemCount;
            syncState.ItemCount = await db.VehicleDayRollups.CountAsync(cancellationToken);
            syncState.LastSyncedAt = now;
            syncState.LastError = null;
            syncState.LastErrorAt = null;
            syncState.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);

            // Only when something moved. This runs hourly through the trading day whether or not
            // a van did anything, and a job that reports the same figure twelve times is how a
            // real change stops being noticed.
            if (syncState.ItemCount != previous)
            {
                logger.LogInformation(
                    "Fleet telematics rollup: {Rows} vehicle-day row(s), {Delta:+#;-#;0} since the last pass",
                    syncState.ItemCount, syncState.ItemCount - previous);
            }

            return built;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Fleet telematics rollup failed");

            syncState.LastError = Truncate(ex.Message);
            syncState.LastErrorAt = DateTime.UtcNow;
            syncState.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);

            throw;
        }
    }

    public async Task<bool> BuildDayAsync(
        string registration, DateTime tradingDate, CancellationToken cancellationToken)
    {
        var key = TelematicsRegistration.Normalize(registration);

        if (key is null)
        {
            return false;
        }

        var context = await LoadDayContextAsync(tradingDate, [key], cancellationToken);

        return await BuildOneAsync(key, tradingDate, context, cancellationToken);
    }

    // — The passes ————————————————————————————————————————————————————

    private async Task<(CartrackRollupCheckpoint, int)> RunBackfillAsync(
        CartrackRollupCheckpoint checkpoint, DateTime today, DateTime now, CancellationToken cancellationToken)
    {
        var start = checkpoint.BackfillStartDate
                    ?? today.AddDays(-Math.Max(1, _settings.BackfillDays));

        // Walks forward from the oldest date, so the earliest history lands first and the covered
        // range is a single contiguous block the readiness gate can reason about.
        var next = checkpoint.BackfillThroughDate?.AddDays(1) ?? start;
        var slice = Math.Max(1, _settings.MaxBackfillDaysPerRun);
        var built = 0;
        var reached = checkpoint.BackfillThroughDate;

        for (var day = 0; day < slice && next < today.AddDays(-1); day++, next = next.AddDays(1))
        {
            var pass = await BuildDateAsync(next, cancellationToken);
            built += pass.Rows;

            // Stop where the reading stopped. Walking on past a date the provider would not
            // answer for would mark it backfilled and never come back to it, and the readiness
            // gate would then claim a covered range with a hole in it.
            if (!pass.MovementLoaded)
            {
                break;
            }

            reached = next;
        }

        var complete = reached is { } r && r >= today.AddDays(-2);

        return (checkpoint with
        {
            BackfillStartDate = start,
            BackfillThroughDate = reached ?? start,
            BackfillCompleted = complete,
            BackfillCompletedAtUtc = complete ? checkpoint.BackfillCompletedAtUtc ?? now : null
        }, built);
    }

    /// <summary>
    /// Builds every vehicle that should have run on one trading date.
    /// </summary>
    /// <returns>
    /// How many rows were written, and whether the fleet-wide movement read actually succeeded.
    /// The caller needs the second: a date that wrote rows from a failed read has not been
    /// reconciled, however many rows it touched.
    /// </returns>
    private async Task<DateBuildResult> BuildDateAsync(
        DateTime tradingDate, CancellationToken cancellationToken)
    {
        var registrations = await RegistrationsForAsync(tradingDate, cancellationToken);

        if (registrations.Count == 0)
        {
            // Nothing to ask about is not a failure: there is no route running that day.
            return new DateBuildResult(0, true);
        }

        var context = await LoadDayContextAsync(tradingDate, registrations, cancellationToken);
        var built = 0;

        foreach (var registration in registrations)
        {
            if (await BuildOneAsync(registration, tradingDate, context, cancellationToken))
            {
                built++;
            }
        }

        return new DateBuildResult(built, context.MovementLoaded);
    }

    /// <summary>What one date's pass achieved.</summary>
    private readonly record struct DateBuildResult(int Rows, bool MovementLoaded);

    /// <summary>
    /// Which vehicles to ask about on a date: the trucks actually recorded on that day's route
    /// days, plus the default truck of every active route.
    /// </summary>
    /// <remarks>
    /// Deliberately not the whole fleet. The request cost is per vehicle per day, and a vehicle no
    /// route runs has nothing to say to this report. The route defaults are included so a day
    /// where the rep never tapped Start Day is still checked against the truck their route runs —
    /// which is the case the report most wants to catch.
    /// </remarks>
    private async Task<List<string>> RegistrationsForAsync(
        DateTime tradingDate, CancellationToken cancellationToken)
    {
        var onTheDay = await db.VanRouteDays
            .AsNoTracking()
            .Where(day => day.TradingDate == tradingDate.Date && day.TruckRegNo != null)
            .Select(day => day.TruckRegNo!)
            .Distinct()
            .ToListAsync(cancellationToken);

        var routeDefaults = await db.Routes
            .AsNoTracking()
            .Where(route => route.IsActive && route.TruckRegNo != null)
            .Select(route => route.TruckRegNo!)
            .Distinct()
            .ToListAsync(cancellationToken);

        return onTheDay
            .Concat(routeDefaults)
            .Select(TelematicsRegistration.Normalize)
            .Where(registration => registration is not null)
            .Select(registration => registration!)
            .Distinct(TelematicsRegistration.Comparer)
            .OrderBy(registration => registration, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The two fleet-wide calls for a date, made once and shared by every vehicle on it.
    /// </summary>
    /// <remarks>
    /// The events window is exactly one CAT day, which is exactly the provider's per-call cap, so
    /// one date is one request rather than one per vehicle. Activity supplies driving and idle
    /// seconds only — its ignition times are Cartrack's own day bucket and the authoritative ones
    /// are read out of the events window below.
    /// </remarks>
    private async Task<DayContext> LoadDayContextAsync(
        DateTime tradingDate, IReadOnlyCollection<string> registrations, CancellationToken cancellationToken)
    {
        var (fromUtc, toUtc) = CartrackTime.UtcWindowOf(tradingDate);
        var context = new DayContext(tradingDate, fromUtc, toUtc);

        try
        {
            context.Activity = await client.GetActivityAsync(tradingDate, cancellationToken);
            context.Events = await client.GetEventsAsync(fromUtc, toUtc, null, cancellationToken);
            context.MovementLoaded = true;
        }
        catch (CartrackRateLimitedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Fleet telematics could not read movement for {TradingDate:yyyy-MM-dd}; "
                + "the day is left incomplete and the next pass will try again.",
                tradingDate);

            context.Error = ex.Message;
        }

        context.Departures = await db.VanRouteDays
            .AsNoTracking()
            .Where(day => day.TradingDate == tradingDate.Date
                          && day.DepartedLatitude != null
                          && day.DepartedLongitude != null)
            .Select(day => new { day.TruckRegNo, day.DepartedLatitude, day.DepartedLongitude })
            .ToDictionaryAsync(
                day => TelematicsRegistration.Normalize(day.TruckRegNo) ?? string.Empty,
                day => (day.DepartedLatitude!.Value, day.DepartedLongitude!.Value),
                TelematicsRegistration.Comparer,
                cancellationToken);

        context.Vehicles = await db.TelematicsVehicles
            .AsTracking()
            .Where(vehicle => registrations.Contains(vehicle.RegistrationNormalized))
            .ToDictionaryAsync(
                vehicle => vehicle.RegistrationNormalized,
                TelematicsRegistration.Comparer,
                cancellationToken);

        context.Limits = await LoadColdChainLimitsAsync(tradingDate, cancellationToken);

        return context;
    }

    /// <summary>
    /// The cold-chain limits each truck ran under on a date: the route it was recorded on that
    /// day, else the route whose default truck it is.
    /// </summary>
    /// <remarks>
    /// Where two routes claim one truck, the first one that has limits set wins, in route-code
    /// order so the choice is the same on every rebuild. A truck judged against no limits at all
    /// because the wrong route was picked would be the worse mistake.
    /// </remarks>
    private async Task<Dictionary<string, ColdChainLimits>> LoadColdChainLimitsAsync(
        DateTime tradingDate, CancellationToken cancellationToken)
    {
        var onTheDay = await db.VanRouteDays
            .AsNoTracking()
            .Where(day => day.TradingDate == tradingDate.Date
                          && day.TruckRegNo != null
                          && day.Route != null)
            .Select(day => new RouteLimitsRow(
                day.TruckRegNo!,
                day.Route!.Code,
                day.Route.TemperatureMinC,
                day.Route.TemperatureMaxC,
                day.Route.TemperatureProbeChannel))
            .ToListAsync(cancellationToken);

        var routeDefaults = await db.Routes
            .AsNoTracking()
            .Where(route => route.IsActive && route.TruckRegNo != null)
            .Select(route => new RouteLimitsRow(
                route.TruckRegNo!,
                route.Code,
                route.TemperatureMinC,
                route.TemperatureMaxC,
                route.TemperatureProbeChannel))
            .ToListAsync(cancellationToken);

        var limits = new Dictionary<string, ColdChainLimits>(TelematicsRegistration.Comparer);

        // The day's own route first, so a truck that ran a different round from its usual one is
        // judged by the round it actually ran.
        foreach (var source in new[] { onTheDay, routeDefaults })
        {
            var byTruck = source
                .Select(row => (Key: TelematicsRegistration.Normalize(row.TruckRegNo), Row: row))
                .Where(entry => entry.Key is not null)
                .GroupBy(entry => entry.Key!, TelematicsRegistration.Comparer);

            foreach (var truck in byTruck)
            {
                if (limits.ContainsKey(truck.Key))
                {
                    continue;
                }

                var chosen = truck
                    .Select(entry => entry.Row)
                    .OrderBy(row => row.MinC is null && row.MaxC is null)
                    .ThenBy(row => row.RouteCode, StringComparer.Ordinal)
                    .First();

                limits[truck.Key] = new ColdChainLimits(chosen.MinC, chosen.MaxC, chosen.Channel);
            }
        }

        return limits;
    }

    private sealed record RouteLimitsRow(
        string TruckRegNo, string RouteCode, decimal? MinC, decimal? MaxC, byte? Channel);

    private async Task<bool> BuildOneAsync(
        string registration, DateTime tradingDate, DayContext context, CancellationToken cancellationToken)
    {
        var row = await db.VehicleDayRollups
            .SingleOrDefaultAsync(
                rollup => rollup.RegistrationNormalized == registration
                          && rollup.TradingDate == tradingDate.Date,
                cancellationToken);

        if (row is null)
        {
            row = new VehicleDayRollupEntity
            {
                RegistrationNormalized = registration,
                TradingDate = tradingDate.Date
            };

            db.VehicleDayRollups.Add(row);
        }

        row.AttemptCount++;
        row.BuiltAtUtc = DateTime.UtcNow;
        row.LastError = Truncate(context.Error);

        ApplyMovement(row, registration, context);
        await ApplyOdometerAsync(row, registration, context, cancellationToken);
        await ApplyFuelAsync(row, registration, context, cancellationToken);
        await ApplyTemperatureAsync(row, registration, context, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        return true;
    }

    /// <summary>Ignition, departure and the day's durations, from the events window and activity.</summary>
    private void ApplyMovement(VehicleDayRollupEntity row, string registration, DayContext context)
    {
        if (!context.MovementLoaded)
        {
            // Deliberately leaves HasActivity as it stands rather than setting it false. A failed
            // re-read must not downgrade a day that was already built: the figures below are
            // still the ones the provider gave us, and they are still true.
            //
            // Found the hard way. A network outage overnight re-ran four days that had real
            // figures on them — one with 356 km — and turned every flag off, which would have
            // rendered them on the report as "the van did not report". A day we have is not a
            // day we lack, however recently we last confirmed it.
            return;
        }

        var mine = context.Events
            .Where(e => TelematicsRegistration.AreSame(e.Registration, registration))
            .Select(e => (Event: e, At: CartrackTime.ToUtc(e.EventTs)))
            .Where(e => e.At is not null)
            .OrderBy(e => e.At)
            .ToList();

        var ignitionOn = mine
            .Where(e => string.Equals(e.Event.EventDescription, "IGNITION_ON", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var ignitionOff = mine
            .Where(e => string.Equals(e.Event.EventDescription, "IGNITION_OFF", StringComparison.OrdinalIgnoreCase))
            .ToList();

        row.FirstIgnitionOnUtc = ignitionOn.FirstOrDefault().At;
        row.LastIgnitionOffUtc = ignitionOff.LastOrDefault().At;
        row.IgnitionCycleCount = ignitionOn.Count;

        var departure = FirstDepartureOf(mine, registration, context);
        row.FirstDepartureUtc = departure?.At;
        row.FirstDepartureLatitude = departure?.Event.Latitude;
        row.FirstDepartureLongitude = departure?.Event.Longitude;

        var activity = context.Activity
            .FirstOrDefault(a => TelematicsRegistration.AreSame(a.Registration, registration));

        row.DrivingSeconds = activity?.DrivingSeconds;
        row.IdleSeconds = activity?.IdleSeconds;

        row.HasActivity = true;
    }

    /// <summary>
    /// The first event that put the vehicle outside the depot radius of where the day's departure
    /// was recorded — which is what "the van left" means, as opposed to "the key turned".
    /// </summary>
    /// <remarks>
    /// With no recorded departure point there is nothing to measure from, so this falls back to
    /// the first event that moved away from wherever the vehicle started the day. A vehicle that
    /// never left the yard has no departure at all, and null is the right answer: it is a
    /// different finding from leaving late.
    /// </remarks>
    private (CartrackVehicleEvent Event, DateTime? At)? FirstDepartureOf(
        List<(CartrackVehicleEvent Event, DateTime? At)> events, string registration, DayContext context)
    {
        var located = events
            .Where(e => e.Event.Latitude is not null && e.Event.Longitude is not null)
            .ToList();

        if (located.Count == 0)
        {
            return null;
        }

        var origin = context.Departures.TryGetValue(registration, out var recorded)
            ? recorded
            : (located[0].Event.Latitude!.Value, located[0].Event.Longitude!.Value);

        var radius = Math.Max(1, _settings.DepotRadiusMetres);

        foreach (var candidate in located)
        {
            var metres = DistanceMetres(
                origin.Item1, origin.Item2,
                candidate.Event.Latitude!.Value, candidate.Event.Longitude!.Value);

            if (metres > radius)
            {
                return candidate;
            }
        }

        return null;
    }

    private async Task ApplyOdometerAsync(
        VehicleDayRollupEntity row, string registration, DayContext context, CancellationToken cancellationToken)
    {
        try
        {
            var odometer = await client.GetOdometerAsync(
                registration, context.FromUtc, context.ToUtc, cancellationToken);

            row.OdometerStartMetres = odometer?.StartOdometerMetres;
            row.OdometerEndMetres = odometer?.EndOdometerMetres;

            // The provider's own distance, never the difference of the two readings. Observed on
            // this fleet: a vehicle reporting distance 0 with an end reading 4.3 km BELOW its
            // start. Subtracting would store a negative day.
            row.DistanceMetres = odometer?.DistanceMetres is { } metres && metres >= 0 ? metres : null;

            row.OdometerWasReset = odometer?.OdometerReset ?? false;
            row.TerminalChanged = odometer?.TerminalHasChanged ?? false;
            row.HasOdometer = odometer is not null;
        }
        catch (CartrackRateLimitedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Its own catch, so a failing odometer does not discard the movement already read —
            // and, like the movement facet above, it leaves HasOdometer as it stands rather than
            // turning it off. A reading we already have does not stop existing because the next
            // refresh could not reach the provider.
            logger.LogWarning(ex,
                "Fleet telematics could not read the odometer for {Registration} on "
                + "{TradingDate:yyyy-MM-dd}; the rest of the day is kept.",
                registration, context.TradingDate);

            row.LastError = Truncate(ex.Message);
        }
    }

    /// <summary>
    /// Tank level, burn and fills — asked for only where a sensor that could answer is fitted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A vehicle with no fuel sensor is not asked at all and keeps <c>HasFuel</c> false, so the
    /// report can say "no fuel sensor" from the vehicle rather than "fuel not reported" from the
    /// day. Those are different sentences: one is a fitting, the other is a fault.
    /// </para>
    /// <para>
    /// Each call is gated on the sensor that answers it. On this fleet no vehicle has a CAN read,
    /// and the consumption endpoint answers an empty object for all of them, so asking would buy
    /// a request per vehicle per day for a null.
    /// </para>
    /// </remarks>
    private async Task ApplyFuelAsync(
        VehicleDayRollupEntity row, string registration, DayContext context, CancellationToken cancellationToken)
    {
        if (!context.Vehicles.TryGetValue(registration, out var vehicle) || !vehicle.HasAnyFuelSensor)
        {
            return;
        }

        try
        {
            var consumed = vehicle.HasFuelCanbusConsumed
                ? await client.GetFuelConsumedAsync(registration, context.FromUtc, context.ToUtc, cancellationToken)
                : null;

            var level = vehicle.HasFuelCanbusLevel || vehicle.HasFuelAnalogLevel
                ? await client.GetFuelLevelAsync(registration, context.FromUtc, context.ToUtc, cancellationToken)
                : null;

            // De-duplicated on when and how much: the provider lists the same fill twice at times
            // (seen live on 2026-09-14), and counting both would double the day's litres.
            var fills = (await client.GetFuelFillsAsync(
                    registration, context.FromUtc, context.ToUtc, cancellationToken))
                .DistinctBy(fill => (CartrackTime.ToUtc(fill.EventTs), fill.Litres))
                .ToList();

            row.FuelConsumedLitres = consumed?.FuelConsumedLitres;
            row.FuelLevelStartLitres = level?.Start?.Litres;
            row.FuelLevelEndLitres = level?.End?.Litres;
            row.EstimatedFuelUsedLitres = level?.EstimatedFuelUsedLitres;
            // Only a response with readings speaks for calibration. Before a van has run, the
            // level endpoint answers with no readings and "calibrated": false — seen live on the
            // morning of 2026-09-21 on a sender calibrated on every day it had readings — and
            // storing that would mark a working sensor as broken until the day was rebuilt.
            row.FuelIsCalibrated = level?.Start is null && level?.End is null
                ? null
                : level.IsCalibrated;

            // Settled only when both ends are, and every fill between them. One provisional
            // reading makes the day's estimate provisional too, because the estimate is built
            // from the levels and the fills together.
            row.FuelReadingsAccurate = level is null
                ? null
                : level.Start?.IsAccurate == true
                  && level.End?.IsAccurate == true
                  && fills.All(fill => fill.IsAccurate != false);

            row.FuelFillCount = fills.Count;
            row.FuelFilledLitres = fills.Sum(fill => fill.Litres ?? 0m);
            row.HasFuel = true;
        }
        catch (CartrackRateLimitedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Leaves HasFuel as it stands, for the reason ApplyMovement gives.
            logger.LogWarning(ex,
                "Fleet telematics could not read fuel for {Registration} on "
                + "{TradingDate:yyyy-MM-dd}; the rest of the day is kept.",
                registration, context.TradingDate);

            row.LastError = Truncate(ex.Message);
        }
    }

    /// <summary>
    /// The day's probe readings: every one kept as evidence, and one channel judged against the
    /// limits the round carried.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The limits are snapshotted, and a snapshot is never loosened.</b> A day already judged
    /// keeps the limits it was judged against, so widening a route's range this morning cannot
    /// erase last month's breach. A day built before its route had any limits takes them up on
    /// the next rebuild, which is what lets a newly configured route judge the days the
    /// reconciliation window still revisits.
    /// </para>
    /// <para>
    /// Every vehicle on the day is asked, not only the ones known to have a probe. The provider
    /// does not say whether one is fitted, so the only way to learn is to ask — and the only
    /// way the report can say "limits set, no probe readings" is to have asked and been told
    /// nothing.
    /// </para>
    /// </remarks>
    private async Task ApplyTemperatureAsync(
        VehicleDayRollupEntity row, string registration, DayContext context, CancellationToken cancellationToken)
    {
        IReadOnlyList<CartrackTemperatureReading> readings;

        try
        {
            // Clamped to now. Unlike the events and odometer calls, the temperature endpoint
            // refuses a window that ends in the future — a 422, "The end_timestamp must be a date
            // before or equal to now" — so today's day, whose window closes at 22:00Z tonight,
            // would never be read at all while it was still today.
            var toUtc = context.ToUtc < DateTime.UtcNow ? context.ToUtc : DateTime.UtcNow;

            readings = await client.GetTemperaturesAsync(
                context.FromUtc, toUtc, registration, cancellationToken);
        }
        catch (CartrackRateLimitedException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Fleet telematics could not read temperatures for {Registration} on "
                + "{TradingDate:yyyy-MM-dd}; the rest of the day is kept.",
                registration, context.TradingDate);

            row.LastError = Truncate(ex.Message);
            return;
        }

        var samples = SamplesOf(readings, registration, context);
        await StoreSamplesAsync(registration, samples, context, cancellationToken);

        context.Limits.TryGetValue(registration, out var limits);

        if (row.LimitMinC is null && row.LimitMaxC is null)
        {
            row.LimitMinC = limits.MinC;
            row.LimitMaxC = limits.MaxC;
        }

        // The route names the channel when a vehicle has more than one probe; otherwise the
        // lowest-numbered one that reported is the fridge, which is what it is on this fleet.
        var channel = limits.Channel
                      ?? (samples.Count > 0 ? samples.Min(sample => sample.Channel) : null);

        var summary = ColdChainEvaluator.Evaluate(
            samples
                .Where(sample => sample.Channel == channel)
                .Select(sample => new TemperaturePoint(sample.EventAtUtc, sample.TemperatureC))
                .ToList(),
            row.LimitMinC,
            row.LimitMaxC,
            TimeSpan.FromMinutes(Math.Max(1, _settings.TemperatureSampleGapCapMinutes)));

        row.TemperatureChannel = channel;
        row.TemperatureSampleCount = summary.SampleCount;
        row.TemperatureMinC = summary.MinC;
        row.TemperatureMaxC = summary.MaxC;
        row.TemperatureAvgC = summary.AvgC;
        row.TemperatureFirstSampleUtc = summary.FirstSampleUtc;
        row.TemperatureLastSampleUtc = summary.LastSampleUtc;
        row.MinutesAboveMaxLimit = summary.MinutesAboveMax;
        row.MinutesBelowMinLimit = summary.MinutesBelowMin;
        row.HasTemperature = true;

        if (context.Vehicles.TryGetValue(registration, out var vehicle))
        {
            if (samples.Count > 0)
            {
                // Inferred, because the provider has no capability flag for a probe. Once one has
                // reported it has a probe; a silent day later means the fridge was off, not that
                // the probe came out.
                var last = samples.Max(sample => sample.EventAtUtc);

                vehicle.HasTemperatureProbe = true;

                if (vehicle.LastTemperatureSeenAtUtc is null || last > vehicle.LastTemperatureSeenAtUtc)
                {
                    vehicle.LastTemperatureSeenAtUtc = last;
                }
            }
            else
            {
                vehicle.HasTemperatureProbe ??= false;
            }
        }
    }

    /// <summary>
    /// The readings as samples, one per reporting channel. Only channels that carry a value are
    /// kept — on this fleet three of the four never do, and storing their nulls would quadruple
    /// the table for nothing.
    /// </summary>
    private static List<VehicleTemperatureSampleEntity> SamplesOf(
        IReadOnlyList<CartrackTemperatureReading> readings, string registration, DayContext context)
    {
        var samples = new List<VehicleTemperatureSampleEntity>();
        var seen = new HashSet<(byte, DateTime)>();

        foreach (var reading in readings)
        {
            if (!TelematicsRegistration.AreSame(reading.Registration, registration)
                || CartrackTime.ToUtc(reading.EventTs) is not { } at
                || at < context.FromUtc
                || at >= context.ToUtc)
            {
                continue;
            }

            var received = CartrackTime.ToUtc(reading.ReceivedTs);

            foreach (var (channel, value) in new (byte, decimal?)[]
                     {
                         (1, reading.Temp1), (2, reading.Temp2), (3, reading.Temp3), (4, reading.Temp4)
                     })
            {
                if (value is not { } celsius || !seen.Add((channel, at)))
                {
                    continue;
                }

                samples.Add(new VehicleTemperatureSampleEntity
                {
                    RegistrationNormalized = registration,
                    TradingDate = CartrackTime.TradingDateOf(at),
                    Channel = channel,
                    EventAtUtc = at,
                    ReceivedAtUtc = received,
                    TemperatureC = celsius
                });
            }
        }

        return samples;
    }

    /// <summary>
    /// Adds the readings not already held. Existing ones are left exactly as they are: this table
    /// is evidence rather than a projection, and a reading is not revised by being read again.
    /// </summary>
    private async Task StoreSamplesAsync(
        string registration,
        List<VehicleTemperatureSampleEntity> samples,
        DayContext context,
        CancellationToken cancellationToken)
    {
        if (samples.Count == 0)
        {
            return;
        }

        var held = await db.VehicleTemperatureSamples
            .AsNoTracking()
            .Where(sample => sample.RegistrationNormalized == registration
                             && sample.EventAtUtc >= context.FromUtc
                             && sample.EventAtUtc < context.ToUtc)
            .Select(sample => new { sample.Channel, sample.EventAtUtc })
            .ToListAsync(cancellationToken);

        var existing = held
            .Select(sample => (sample.Channel, sample.EventAtUtc))
            .ToHashSet();

        db.VehicleTemperatureSamples.AddRange(
            samples.Where(sample => !existing.Contains((sample.Channel, sample.EventAtUtc))));
    }

    /// <summary>
    /// Great-circle distance in metres. Good to well under a metre at these ranges, which is far
    /// finer than a depot radius needs.
    /// </summary>
    private static double DistanceMetres(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthRadiusMetres = 6_371_000;

        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;

        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180)
                  * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);

        return earthRadiusMetres * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    // — Checkpoint and state ——————————————————————————————————————————

    private async Task<(SystemConfigEntity Row, CartrackRollupCheckpoint Checkpoint)> GetCheckpointAsync(
        DateTime now, DateTime today, CancellationToken cancellationToken)
    {
        var row = await db.SystemConfigs
            .AsTracking()
            .SingleOrDefaultAsync(config => config.Key == CheckpointConfigKey, cancellationToken);

        if (row is null)
        {
            row = new SystemConfigEntity
            {
                Key = CheckpointConfigKey,
                ValueType = "json",
                Category = "Synchronization",
                Description = "Resumable fleet telematics day-rollup checkpoint.",
                IsEditable = false,
                UpdatedAt = now
            };

            db.SystemConfigs.Add(row);
            await db.SaveChangesAsync(cancellationToken);
        }

        var checkpoint = CartrackRollupCheckpointReader.TryRead(row.Value);

        if (checkpoint is null)
        {
            // Reset rather than throw: an unreadable checkpoint costs a backfill, while refusing
            // to run costs every night until somebody notices.
            logger.LogWarning("Resetting an unreadable fleet telematics rollup checkpoint");

            checkpoint = new CartrackRollupCheckpoint
            {
                BackfillStartDate = today.AddDays(-Math.Max(1, _settings.BackfillDays))
            };
        }

        return (row, checkpoint);
    }

    private async Task SaveCheckpointAsync(
        SystemConfigEntity row,
        CartrackRollupCheckpoint checkpoint,
        DateTime now,
        CancellationToken cancellationToken)
    {
        row.Value = JsonSerializer.Serialize(checkpoint);
        row.UpdatedAt = now;

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<CacheSyncStateEntity> GetOrCreateSyncStateAsync(CancellationToken cancellationToken)
    {
        var state = await db.CacheSyncStates
            .AsTracking()
            .SingleOrDefaultAsync(entry => entry.CacheKey == CacheKey, cancellationToken);

        if (state is not null)
        {
            return state;
        }

        state = new CacheSyncStateEntity
        {
            CacheKey = CacheKey,
            DisplayName = DisplayName,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.CacheSyncStates.Add(state);

        return state;
    }

    private static string? Truncate(string? value) =>
        value is null || value.Length <= MaxErrorLength ? value : value[..MaxErrorLength];

    /// <summary>The limits one truck's round carried, and which probe channel is its fridge. All null when unset.</summary>
    private readonly record struct ColdChainLimits(decimal? MinC, decimal? MaxC, byte? Channel);

    /// <summary>The two fleet-wide reads for one date, plus where each van's day was recorded from.</summary>
    private sealed class DayContext(DateTime tradingDate, DateTime fromUtc, DateTime toUtc)
    {
        public DateTime TradingDate { get; } = tradingDate;
        public DateTime FromUtc { get; } = fromUtc;
        public DateTime ToUtc { get; } = toUtc;

        public IReadOnlyList<CartrackVehicleActivity> Activity { get; set; } = [];
        public IReadOnlyList<CartrackVehicleEvent> Events { get; set; } = [];

        /// <summary>False when the movement read failed, which is not the same as a quiet day.</summary>
        public bool MovementLoaded { get; set; }

        public Dictionary<string, (double, double)> Departures { get; set; } = new(TelematicsRegistration.Comparer);

        /// <summary>Each vehicle's fleet record — which sensors it has — tracked so the probe inference is saved.</summary>
        public Dictionary<string, TelematicsVehicleEntity> Vehicles { get; set; } = new(TelematicsRegistration.Comparer);

        public Dictionary<string, ColdChainLimits> Limits { get; set; } = new(TelematicsRegistration.Comparer);

        public string? Error { get; set; }
    }
}
