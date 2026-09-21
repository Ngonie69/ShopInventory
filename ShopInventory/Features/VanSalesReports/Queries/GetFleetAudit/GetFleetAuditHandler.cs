using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Telematics;
using ShopInventory.Data;
using ShopInventory.Features.VanSalesReports.Queries;
using ShopInventory.Features.VanSalesReports.Queries.GetDepartureComplianceReport;
using ShopInventory.Models.Entities;
using ShopInventory.Services;
using ShopInventory.Services.Telematics;

namespace ShopInventory.Features.VanSalesReports.Queries.GetFleetAudit;

/// <summary>
/// The fleet over a period: a row per vehicle, each carrying its own days.
/// </summary>
/// <remarks>
/// <para>
/// Both halves are returned in one call rather than a list endpoint plus a detail endpoint per
/// vehicle. The fleet is small — single figures — and the days are what the summary is computed
/// from, so fetching them separately would mean either computing the totals twice or trusting
/// two calls to agree.
/// </para>
/// <para>
/// <b>A day with no rollup row is still a day.</b> The vehicle is listed for every trading day in
/// the period, so a truck that reported nothing for a fortnight shows a fortnight of silence
/// rather than an empty list — which reads as a short period rather than a dead tracker.
/// </para>
/// </remarks>
public sealed class GetFleetAuditHandler(
    ApplicationDbContext db,
    ICartrackReadService telematics
) : IRequestHandler<GetFleetAuditQuery, ErrorOr<FleetAuditResult>>
{
    /// <summary>
    /// The same ceiling the compliance report uses. A fleet page returns a day per vehicle, so
    /// the row count is the period times the fleet — an unbounded range is a slow page and a
    /// large payload rather than an error somebody notices.
    /// </summary>
    private const int MaximumDays = 400;

    public async Task<ErrorOr<FleetAuditResult>> Handle(
        GetFleetAuditQuery query, CancellationToken cancellationToken)
    {
        var from = query.FromDate.Date;
        var to = query.ToDate.Date;

        if (to < from)
        {
            return Error.Validation("FleetAudit.InvalidRange", "The end date is before the start date.");
        }

        if ((to - from).TotalDays + 1 > MaximumDays)
        {
            return Error.Validation(
                "FleetAudit.RangeTooWide",
                $"Choose a period of {MaximumDays} days or fewer.");
        }

        var status = await telematics.GetStatusAsync(from, to, cancellationToken);

        var vehicles = await db.TelematicsVehicles
            .AsNoTracking()
            .Where(vehicle => query.Registration == null
                              || vehicle.RegistrationNormalized == query.Registration)
            .OrderByDescending(vehicle => vehicle.IsActiveInFleet)
            .ThenBy(vehicle => vehicle.RegistrationNormalized)
            .ToListAsync(cancellationToken);

        if (vehicles.Count == 0)
        {
            return new FleetAuditResult(from, to, [], StatusDto(status));
        }

        var plates = vehicles.Select(vehicle => vehicle.RegistrationNormalized).ToList();

        var rollups = await db.VehicleDayRollups
            .AsNoTracking()
            .Where(rollup => plates.Contains(rollup.RegistrationNormalized)
                             && rollup.TradingDate >= from
                             && rollup.TradingDate <= to)
            .ToListAsync(cancellationToken);

        // Who was driving, and on what round. Keyed the same way the compliance report keys its
        // own join, so the two pages cannot disagree about which rep ran which truck.
        var routeDays = await db.VanRouteDays
            .AsNoTracking()
            .Where(day => day.TradingDate >= from && day.TradingDate <= to && day.TruckRegNo != null)
            .Select(day => new
            {
                day.TruckRegNo,
                day.TradingDate,
                day.RouteName,
                day.Username,
                Rep = day.User == null ? null : day.User.FirstName + " " + day.User.LastName,
                day.StartingMileage,
                day.ClosingMileage
            })
            .ToListAsync(cancellationToken);

        var byVehicleDay = routeDays
            .Select(day => new
            {
                Key = TelematicsRegistration.Normalize(day.TruckRegNo),
                day.TradingDate,
                day.RouteName,
                Rep = string.IsNullOrWhiteSpace(day.Rep?.Trim()) ? day.Username : day.Rep!.Trim(),
                Km = day.StartingMileage is { } start && day.ClosingMileage is { } close && close >= start
                    ? close - start
                    : (int?)null
            })
            .Where(day => day.Key is not null)
            .GroupBy(day => (day.Key!, day.TradingDate))
            // A truck can carry two reps on one day if a route was reassigned. First wins, so the
            // page is stable rather than depending on row order.
            .ToDictionary(group => group.Key, group => group.First());

        // The takings of the van accounts these trucks run for. Read through VanSalesFactReader
        // rather than off DesktopSales directly: an online van sale posts straight to SAP and
        // leaves only a confirmed StockReservation behind, so reading either table alone
        // under-reports — silently. That union is the reader's whole reason for existing.
        var linkedAccounts = vehicles
            .Select(vehicle => vehicle.BusinessPartnerCode)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code!.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var salesByAccountDay = linkedAccounts.Count == 0
            ? []
            : (await VanSalesFactReader.LoadSalesAsync(
                    db, new VanSalesFactFilter(from, to), cancellationToken))
                .Where(fact => linkedAccounts.Contains(fact.VanAccountCode))
                .GroupBy(fact => (Account: fact.VanAccountCode.Trim(), fact.TradingDate))
                .ToDictionary(
                    group => group.Key,
                    group => (
                        Total: group.Sum(fact => fact.TotalAmount),
                        Count: group.Count(),
                        Currency: group
                            .Select(fact => fact.Currency)
                            .FirstOrDefault(currency => !string.IsNullOrWhiteSpace(currency))),
                    TupleComparer);

        var rollupsByKey = rollups.ToDictionary(
            rollup => (rollup.RegistrationNormalized, rollup.TradingDate));

        var dates = Dates(from, to).ToList();
        var rows = new List<FleetAuditVehicleDto>(vehicles.Count);

        foreach (var vehicle in vehicles)
        {
            var days = new List<FleetAuditDayDto>(dates.Count);

            foreach (var date in dates)
            {
                rollupsByKey.TryGetValue((vehicle.RegistrationNormalized, date), out var rollup);
                byVehicleDay.TryGetValue((vehicle.RegistrationNormalized, date), out var driven);

                var sold = vehicle.BusinessPartnerCode is { } account
                           && salesByAccountDay.TryGetValue((account.Trim(), date), out var takings)
                    ? takings
                    : default;

                days.Add(new FleetAuditDayDto(
                    TradingDate: date,
                    HasRollup: rollup is not null,
                    MovementRead: rollup?.HasActivity ?? false,
                    OdometerRead: rollup?.HasOdometer ?? false,
                    FirstIgnitionOn: Cat(rollup?.FirstIgnitionOnUtc),
                    FirstDeparture: Cat(rollup?.FirstDepartureUtc),
                    LastIgnitionOff: Cat(rollup?.LastIgnitionOffUtc),
                    IgnitionCycleCount: rollup?.IgnitionCycleCount,
                    DrivingMinutes: rollup?.DrivingSeconds is { } drive ? drive / 60 : null,
                    IdleMinutes: rollup?.IdleSeconds is { } idle ? idle / 60 : null,
                    DistanceKm: rollup?.DistanceMetres is { } metres
                        ? (int)Math.Round(metres / 1000.0)
                        : null,
                    OdometerReset: rollup?.OdometerWasReset ?? false,
                    TerminalChanged: rollup?.TerminalChanged ?? false,
                    LastError: rollup?.LastError,
                    RepName: driven?.Rep,
                    RouteName: driven?.RouteName,
                    RepOdometerKm: driven?.Km,
                    // Null rather than zero where no account is linked: nobody can attribute
                    // this van's takings, which is different from a van that sold nothing.
                    Sales: vehicle.BusinessPartnerCode is null ? null : sold.Total,
                    SaleCount: vehicle.BusinessPartnerCode is null ? null : sold.Count,
                    Fuel: FuelOf(rollup),
                    Temperature: TemperatureOf(rollup)));
            }

            // Counted over the days that reported, so a fortnight of silence does not drag an
            // average down as though the truck had been standing still.
            var withData = days.Where(day => day.HasRollup && day.MovementRead).ToList();
            var moved = withData.Where(day => day.FirstDeparture is not null).ToList();
            // Days with a fuel figure. A day read successfully but with nothing in it — today
            // before the van has run — adds nothing to the total and so must not taint it.
            var fuelDays = days
                .Where(day => day.Fuel is { } fuel
                              && (fuel.UsedLitres is not null
                                  || fuel.ConsumedLitres is not null
                                  || fuel.LevelStartLitres is not null
                                  || fuel.LevelEndLitres is not null))
                .Select(day => day.Fuel!)
                .ToList();
            var coldDays = days.Where(day => day.Temperature is not null).Select(day => day.Temperature!).ToList();
            var readDays = coldDays.Where(day => day.SampleCount > 0).ToList();
            var judgedDays = coldDays.Where(day => day.Judged).ToList();

            rows.Add(new FleetAuditVehicleDto(
                Registration: vehicle.Registration ?? vehicle.RegistrationNormalized,
                RegistrationNormalized: vehicle.RegistrationNormalized,
                VehicleName: vehicle.ClientVehicleName,
                Description: Describe(vehicle),
                BusinessPartnerCode: vehicle.BusinessPartnerCode,
                BusinessPartnerName: vehicle.BusinessPartnerName,
                IsActiveInFleet: vehicle.IsActiveInFleet,
                StateLabel: StateOf(vehicle),
                HasAnyFuelSensor: vehicle.HasAnyFuelSensor,
                HasTemperatureProbe: vehicle.HasTemperatureProbe,
                LastTemperatureSeenAt: Cat(vehicle.LastTemperatureSeenAtUtc),
                LastReportedAt: withData
                    .Select(day => day.LastIgnitionOff ?? day.FirstIgnitionOn)
                    .Where(at => at is not null)
                    .DefaultIfEmpty(null)
                    .Max(),

                DaysWithData: withData.Count,
                DaysSilent: days.Count - withData.Count,
                DaysMoved: moved.Count,
                TotalDistanceKm: withData.Sum(day => day.DistanceKm ?? 0),
                TotalDrivingMinutes: withData.Sum(day => day.DrivingMinutes ?? 0),
                TotalIdleMinutes: withData.Sum(day => day.IdleMinutes ?? 0),
                TotalIgnitionCycles: withData.Sum(day => day.IgnitionCycleCount ?? 0),
                EarliestDeparture: moved.Count == 0 ? null : moved.Min(day => day.FirstDeparture),
                LatestDeparture: moved.Count == 0 ? null : moved.Max(day => day.FirstDeparture),

                // Over every day in the period, not only the days the tracker reported: a sale
                // is a fact about the van whether or not the truck was talking to us.
                TotalSales: days.Sum(day => day.Sales ?? 0m),
                SaleCount: days.Sum(day => day.SaleCount ?? 0),
                Currency: vehicle.BusinessPartnerCode is { } code
                    ? salesByAccountDay
                        .Where(entry => string.Equals(
                            entry.Key.Account, code.Trim(), StringComparison.OrdinalIgnoreCase))
                        .Select(entry => entry.Value.Currency)
                        .FirstOrDefault(currency => !string.IsNullOrWhiteSpace(currency))
                    : null,

                DaysWithFuel: fuelDays.Count,
                // Null rather than zero when no day gave a figure: nobody measured it, which is
                // not the same as a van that burned nothing.
                FuelUsedLitres: fuelDays.Any(day => day.UsedLitres is not null)
                    ? fuelDays.Sum(day => day.UsedLitres ?? 0m)
                    : null,
                FuelAllTrustworthy: fuelDays.Count > 0 && fuelDays.All(day => day.Trustworthy),
                FuelFillCount: fuelDays.Sum(day => day.FillCount),
                FuelFilledLitres: fuelDays.Sum(day => day.FilledLitres),

                DaysWithTemperature: coldDays.Count,
                DaysWithReadings: readDays.Count,
                DaysBreached: coldDays.Count(day => day.Breached),
                MinutesOutsideLimits: judgedDays.Count == 0
                    ? null
                    : judgedDays.Sum(day => day.MinutesOutside ?? 0),
                TemperatureMinC: readDays.Count == 0 ? null : readDays.Min(day => day.MinC),
                TemperatureMaxC: readDays.Count == 0 ? null : readDays.Max(day => day.MaxC),

                // Newest first, which is the order somebody auditing a truck reads it in.
                Days: days.OrderByDescending(day => day.TradingDate).ToList()));
        }

        return new FleetAuditResult(from, to, rows, StatusDto(status));
    }

    /// <summary>
    /// Case-insensitive on the account code. SAP spells a CardCode one way, but it reaches this
    /// system through a field an administrator typed, and a dictionary that cares about case
    /// would drop the takings of a van linked as "van010".
    /// </summary>
    private static readonly IEqualityComparer<(string Account, DateTime TradingDate)> TupleComparer =
        new AccountDayComparer();

    private sealed class AccountDayComparer : IEqualityComparer<(string Account, DateTime TradingDate)>
    {
        public bool Equals((string Account, DateTime TradingDate) left,
                           (string Account, DateTime TradingDate) right) =>
            left.TradingDate == right.TradingDate
            && string.Equals(left.Account, right.Account, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Account, DateTime TradingDate) value) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Account), value.TradingDate);
    }

    private static IEnumerable<DateTime> Dates(DateTime from, DateTime to)
    {
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            yield return date;
        }
    }

    private static string? Describe(TelematicsVehicleEntity vehicle) =>
        string.Join(" ", new[] { vehicle.Manufacturer, vehicle.Model }
            .Where(part => !string.IsNullOrWhiteSpace(part))) is { Length: > 0 } text
            ? text
            : null;

    private static string? StateOf(TelematicsVehicleEntity vehicle) =>
        !vehicle.IsActiveInFleet ? "no longer in the fleet"
        : vehicle.TerminalInRepair ? "tracker in repair"
        : vehicle.IsUnderMaintenance ? "under maintenance"
        : null;

    private static DateTime? Cat(DateTime? utc) => utc is null ? null : AuditService.ToCAT(utc.Value);

    /// <summary>Null unless the fuel read succeeded — see <see cref="FleetAuditFuelDto"/>.</summary>
    private static FleetAuditFuelDto? FuelOf(VehicleDayRollupEntity? rollup) =>
        rollup is not { HasFuel: true }
            ? null
            : new FleetAuditFuelDto(
                LevelStartLitres: rollup.FuelLevelStartLitres,
                LevelEndLitres: rollup.FuelLevelEndLitres,
                UsedLitres: rollup.EstimatedFuelUsedLitres,
                ConsumedLitres: rollup.FuelConsumedLitres,
                IsCalibrated: rollup.FuelIsCalibrated,
                ReadingsSettled: rollup.FuelReadingsAccurate,
                FillCount: rollup.FuelFillCount ?? 0,
                FilledLitres: rollup.FuelFilledLitres ?? 0m);

    /// <summary>Null unless the temperature read succeeded — see <see cref="FleetAuditTemperatureDto"/>.</summary>
    private static FleetAuditTemperatureDto? TemperatureOf(VehicleDayRollupEntity? rollup) =>
        rollup is not { HasTemperature: true }
            ? null
            : new FleetAuditTemperatureDto(
                Channel: rollup.TemperatureChannel,
                SampleCount: rollup.TemperatureSampleCount ?? 0,
                MinC: rollup.TemperatureMinC,
                MaxC: rollup.TemperatureMaxC,
                AvgC: rollup.TemperatureAvgC,
                FirstSampleAt: Cat(rollup.TemperatureFirstSampleUtc),
                LastSampleAt: Cat(rollup.TemperatureLastSampleUtc),
                LimitMinC: rollup.LimitMinC,
                LimitMaxC: rollup.LimitMaxC,
                MinutesAboveMax: rollup.MinutesAboveMaxLimit,
                MinutesBelowMin: rollup.MinutesBelowMinLimit);

    private static DepartureComplianceTelematicsStatusDto StatusDto(CartrackRollupStatus status) =>
        new(status.Enabled,
            status.Configured,
            status.Ready,
            status.LastSyncedAtUtc is { } synced ? AuditService.ToCAT(synced) : null,
            status.CoveredFrom,
            status.CoveredThrough,
            status.Reason);
}
