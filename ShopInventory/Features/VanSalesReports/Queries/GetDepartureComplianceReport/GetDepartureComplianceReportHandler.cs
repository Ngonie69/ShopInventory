using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models.Entities;
using ShopInventory.Common.Telematics;
using ShopInventory.Services;
using ShopInventory.Services.Telematics;

namespace ShopInventory.Features.VanSalesReports.Queries.GetDepartureComplianceReport;

/// <summary>
/// Builds the departure compliance report: one row per rep per trading day.
///
/// Three sources have to agree on what a day is, and they keep time differently, which is the whole
/// difficulty here:
///
/// - <c>VanRouteDays</c> carries a bare CAT <c>TradingDate</c>, stated by the handset.
/// - <c>TimesheetEntries</c> carries UTC instants, so a visit's day is the CAT date of its check-in.
/// - <c>DesktopSales.DocDate</c> is a bare CAT trading day, but <c>StockReservations.CreatedAt</c> is
///   a UTC instant — so the two halves of "what was sold" need different conversions to land on the
///   same day. Ignoring that puts every evening sale after 22:00 CAT on the previous day.
///
/// The sales are read from both tables because there is no single one holding them all: an offline
/// van sale becomes a <c>DesktopSale</c>, while an online direct invoice posts straight to SAP and
/// leaves only a confirmed <c>StockReservation</c> behind. Reading either alone under-reports, and
/// does so silently. That union, the clock conversion and the rep attribution all live in
/// <see cref="VanSalesFactReader"/> so every other van sales report reads exactly what this one
/// counts — if the two ever disagree it is a bug in one shared place, not a discrepancy to hunt.
/// </summary>
public sealed class GetDepartureComplianceReportHandler(
    ApplicationDbContext db,
    ICartrackReadService telematics
) : IRequestHandler<GetDepartureComplianceReportQuery, ErrorOr<DepartureComplianceReportResult>>
{
    /// <summary>A day that produced neither a departure record, a visit nor a sale is not a day.</summary>
    private const int MaximumDays = VanSalesFacts.MaximumDays;

    public async Task<ErrorOr<DepartureComplianceReportResult>> Handle(
        GetDepartureComplianceReportQuery query,
        CancellationToken cancellationToken)
    {
        var from = query.FromDate.Date;
        var to = query.ToDate.Date;

        if (to < from)
        {
            return Error.Validation(
                "VanSalesReports.InvalidRange",
                "The end of the period cannot be before its start.");
        }

        if ((to - from).TotalDays > MaximumDays)
        {
            return Error.Validation(
                "VanSalesReports.RangeTooWide",
                $"Choose a period of {MaximumDays} days or fewer.");
        }

        // The UTC window the CAT day range corresponds to, for the two tables that store instants.
        var (windowStartUtc, windowEndUtc) = VanSalesFacts.ToUtcWindow(from, to);

        var days = await LoadDaysAsync(query, from, to, cancellationToken);
        var visits = await LoadVisitsAsync(query, windowStartUtc, windowEndUtc, cancellationToken);
        var sales = await LoadSalesAsync(query, from, to, cancellationToken);
        var newCustomers = await LoadNewCustomersAsync(windowStartUtc, windowEndUtc, cancellationToken);

        // Every key any of the three knows about. A rep who checked in but never opened a day still
        // gets a row — the missing departure record is itself the finding, and dropping the row would
        // hide it.
        var keys = days.Keys
            .Concat(visits.Keys)
            .Concat(sales.Keys)
            .Distinct()
            .ToList();

        var names = await LoadUserNamesAsync(keys.Select(key => key.UserId).Distinct(), cancellationToken);

        // The truck each row should be checked against: what the day recorded, or failing that the
        // default truck of the rep's own route. The second is what lets a day where the rep never
        // tapped Start Day still be answered for by a vehicle — which is the case this report most
        // wants to catch.
        var routeTrucks = await LoadRouteTrucksAsync(
            keys.Select(key => key.UserId).Distinct(), cancellationToken);

        var plates = keys
            .Select(key => PlateFor(days, routeTrucks, key))
            .Where(plate => plate is not null)
            .Select(plate => plate!)
            .Distinct(TelematicsRegistration.Comparer)
            .ToList();

        var status = await telematics.GetStatusAsync(from, to, cancellationToken);

        // Only load the projection when it can actually speak for the period. Reading it while the
        // backfill is short would put real-looking figures on some rows and nothing on others, and
        // a reader has no way to tell that apart from vans that did not move.
        var rollups = status.Ready
            // One day earlier than asked, because a rep who tags a round to the wrong calendar day
            // leaves the vehicle's movement on the date before the one the handset claims.
            ? await telematics.LoadRollupsAsync(plates, from.AddDays(-1), to, cancellationToken)
            : [];

        var fleet = status.Enabled && status.Configured
            ? await telematics.LoadVehiclesAsync(plates, cancellationToken)
            : [];

        var rows = new List<DepartureComplianceDayDto>(keys.Count);

        foreach (var key in keys)
        {
            days.TryGetValue(key, out var day);
            visits.TryGetValue(key, out var visit);
            sales.TryGetValue(key, out var sale);
            newCustomers.TryGetValue(key, out var newCustomerCount);
            names.TryGetValue(key.UserId, out var name);

            // A route filter that matched no day must not then be satisfied by loose visits: the
            // visit tables carry no route, so a row with no day record cannot be shown to belong to
            // the route asked for.
            if (!string.IsNullOrWhiteSpace(query.RouteCode) && day is null)
            {
                continue;
            }

            var telematicsDay = status.Enabled
                ? BuildTelematics(
                    PlateFor(days, routeTrucks, key), key, day, rollups, fleet, status)
                : null;

            rows.Add(BuildRow(key, day, visit, sale, newCustomerCount, name, telematicsDay));
        }

        var ordered = rows
            .OrderByDescending(row => row.TradingDate)
            .ThenBy(row => row.FullName ?? row.Username, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new DepartureComplianceReportResult(
            from,
            to,
            ordered,
            Summarise(ordered),
            new DepartureComplianceTelematicsStatusDto(
                status.Enabled,
                status.Configured,
                status.Ready,
                status.LastSyncedAtUtc is { } synced ? AuditService.ToCAT(synced) : null,
                status.CoveredFrom,
                status.CoveredThrough,
                status.Reason));
    }

    private static DepartureComplianceDayDto BuildRow(
        DayKey key,
        VanRouteDayEntity? day,
        VisitTotals? visit,
        SaleTotals? sale,
        int newCustomerCount,
        UserName? name,
        DepartureComplianceTelematicsDto? telematics)
    {
        return new DepartureComplianceDayDto(
            VanRouteDayId: day?.Id,
            UserId: key.UserId,
            Username: name?.Username ?? day?.Username ?? visit?.Username ?? key.UserId.ToString(),
            FullName: name?.FullName,
            TradingDate: key.TradingDate,

            Territory: day?.Territory,
            RouteCode: day?.RouteCode,
            RouteName: day?.RouteName,
            TruckRegNo: day?.TruckRegNo,

            // The sheet's "Time out" and "Time In", shown in the clock they were recorded in.
            TimeOut: day is null ? null : AuditService.ToCAT(day.DepartedAt),
            TimeIn: day?.ReturnedAt is { } returned ? AuditService.ToCAT(returned) : null,

            PlannedCustomerCount: day?.PlannedCustomerCount ?? 0,
            CustomersVisited: visit?.CustomersVisited ?? 0,
            ProductiveCalls: sale?.ProductiveCalls ?? 0,

            RtiOut: day?.RtiOut,
            RtiReturned: day?.RtiReturned,

            SystemCash: sale?.Cash ?? 0,
            SystemEcocash: sale?.Ecocash ?? 0,
            SystemInnbucks: sale?.Innbucks ?? 0,
            SystemOther: sale?.Other ?? 0,
            SystemUntendered: sale?.Untendered ?? 0,
            SystemTotalSales: sale?.Total ?? 0,

            DeclaredCash: day?.DeclaredCash,
            DeclaredEcocash: day?.DeclaredEcocash,
            DeclaredInnbucks: day?.DeclaredInnbucks,

            Currency: day?.DeclaredCurrency ?? sale?.Currency,
            NewCustomers: newCustomerCount,

            StartingMileage: day?.StartingMileage,
            ClosingMileage: day?.ClosingMileage,

            HasDayRecord: day is not null,
            IsClosed: day?.IsClosed ?? false,
            Notes: day?.Notes,
            Telematics: telematics);
    }

    private static DepartureComplianceSummary Summarise(List<DepartureComplianceDayDto> rows)
    {
        var kilometres = rows
            .Select(row => row.KilometresTravelled)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToList();

        return new DepartureComplianceSummary(
            DayCount: rows.Count,
            PlannedCustomerCount: rows.Sum(row => row.PlannedCustomerCount),
            CustomersVisited: rows.Sum(row => row.CustomersVisited),
            ProductiveCalls: rows.Sum(row => row.ProductiveCalls),
            TotalSales: rows.Sum(row => row.SystemTotalSales),
            NewCustomers: rows.Sum(row => row.NewCustomers),
            KilometresTravelled: kilometres.Count > 0 ? kilometres.Sum() : null);
    }


    /// <summary>
    /// The truck a row should be checked against: the one the day recorded, or the default on the
    /// rep's own route when there is no day record to have recorded one.
    /// </summary>
    private static string? PlateFor(
        Dictionary<DayKey, VanRouteDayEntity> days,
        Dictionary<Guid, string> routeTrucks,
        DayKey key)
    {
        var recorded = days.TryGetValue(key, out var day) ? day.TruckRegNo : null;

        return TelematicsRegistration.Normalize(recorded)
               ?? (routeTrucks.TryGetValue(key.UserId, out var fallback)
                   ? TelematicsRegistration.Normalize(fallback)
                   : null);
    }

    /// <summary>Each rep's route's default truck, for the rows that have no day record.</summary>
    private async Task<Dictionary<Guid, string>> LoadRouteTrucksAsync(
        IEnumerable<Guid> userIds, CancellationToken cancellationToken)
    {
        var ids = userIds.ToList();

        if (ids.Count == 0)
        {
            return [];
        }

        var pairs = await db.Users
            .AsNoTracking()
            .Where(user => ids.Contains(user.Id)
                           && user.Route != null
                           && user.Route.TruckRegNo != null)
            .Select(user => new { user.Id, Truck = user.Route!.TruckRegNo! })
            .ToListAsync(cancellationToken);

        return pairs.ToDictionary(pair => pair.Id, pair => pair.Truck);
    }

    /// <summary>
    /// What the vehicle says about this rep-day.
    /// </summary>
    /// <remarks>
    /// Returns a record even when there is nothing to report, because the four reasons a row can
    /// be empty — no truck named, a truck the provider does not know, a truck that reported
    /// nothing, and a period the projection cannot speak for — need to be distinguishable on the
    /// page. A null here means one thing only: telematics is off for the whole report.
    /// </remarks>
    private static DepartureComplianceTelematicsDto BuildTelematics(
        string? plate,
        DayKey key,
        VanRouteDayEntity? day,
        Dictionary<(string Registration, DateTime TradingDate), VehicleDayRollupEntity> rollups,
        Dictionary<string, TelematicsVehicleEntity> fleet,
        CartrackRollupStatus status)
    {
        if (plate is null)
        {
            return Empty(null, TelematicsMatch.NoRegistration, null);
        }

        fleet.TryGetValue(plate, out var vehicle);

        if (vehicle is null)
        {
            return Empty(plate, TelematicsMatch.NotInFleet, null);
        }

        var state = !vehicle.IsActiveInFleet ? "no longer in the fleet"
            : vehicle.TerminalInRepair ? "tracker in repair"
            : vehicle.IsUnderMaintenance ? "under maintenance"
            : null;

        // The day the handset tagged the round to first, then the calendar day the van actually
        // departed on. They differ when a rep opens tomorrow's round late tonight, and the
        // vehicle's movement is filed under the day it happened.
        if (!rollups.TryGetValue((plate, key.TradingDate.Date), out var rollup)
            && day is not null
            && AuditService.ToCAT(day.DepartedAt).Date is var departedOn
            && departedOn != key.TradingDate.Date)
        {
            rollups.TryGetValue((plate, departedOn), out rollup);
        }

        if (rollup is null)
        {
            return Empty(plate, TelematicsMatch.Matched, state);
        }

        return new DepartureComplianceTelematicsDto(
            Registration: plate,
            Match: TelematicsMatch.Matched,
            HasRollup: true,
            MovementRead: rollup.HasActivity,
            OdometerRead: rollup.HasOdometer,
            FirstIgnitionOn: Cat(rollup.FirstIgnitionOnUtc),
            FirstDeparture: Cat(rollup.FirstDepartureUtc),
            LastIgnitionOff: Cat(rollup.LastIgnitionOffUtc),
            DepartureLatitude: rollup.FirstDepartureLatitude,
            DepartureLongitude: rollup.FirstDepartureLongitude,
            IgnitionCycleCount: rollup.IgnitionCycleCount,
            DrivingMinutes: rollup.DrivingSeconds is { } driving ? driving / 60 : null,
            IdleMinutes: rollup.IdleSeconds is { } idle ? idle / 60 : null,
            // Metres to whole kilometres, matching the rep's own two whole-kilometre readings.
            DistanceKm: rollup.DistanceMetres is { } metres ? (int)Math.Round(metres / 1000.0) : null,
            OdometerReset: rollup.OdometerWasReset,
            TerminalChanged: rollup.TerminalChanged,
            VehicleStateLabel: state);

        static DepartureComplianceTelematicsDto Empty(string? plate, TelematicsMatch match, string? state) =>
            new(plate, match, false, false, false, null, null, null, null, null,
                null, null, null, null, false, false, state);

        static DateTime? Cat(DateTime? utc) => utc is null ? null : AuditService.ToCAT(utc.Value);
    }

    private async Task<Dictionary<DayKey, VanRouteDayEntity>> LoadDaysAsync(
        GetDepartureComplianceReportQuery query,
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken)
    {
        var queryable = db.VanRouteDays
            .AsNoTracking()
            .Where(day => day.TradingDate >= from && day.TradingDate <= to);

        if (query.UserId.HasValue)
        {
            queryable = queryable.Where(day => day.UserId == query.UserId.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.RouteCode))
        {
            var routeCode = query.RouteCode.Trim();
            queryable = queryable.Where(day => day.RouteCode == routeCode);
        }

        var days = await queryable.ToListAsync(cancellationToken);

        return days.ToDictionary(day => new DayKey(day.UserId, day.TradingDate));
    }

    private async Task<Dictionary<DayKey, VisitTotals>> LoadVisitsAsync(
        GetDepartureComplianceReportQuery query,
        DateTime windowStartUtc,
        DateTime windowEndUtc,
        CancellationToken cancellationToken)
    {
        var queryable = db.TimesheetEntries
            .AsNoTracking()
            .Where(entry => entry.Channel == TimesheetChannel.VanSales
                            && entry.CheckInTime >= windowStartUtc
                            && entry.CheckInTime < windowEndUtc);

        if (query.UserId.HasValue)
        {
            queryable = queryable.Where(entry => entry.UserId == query.UserId.Value);
        }

        var entries = await queryable
            .Select(entry => new { entry.UserId, entry.Username, entry.CheckInTime, entry.CustomerCode })
            .ToListAsync(cancellationToken);

        // Grouped in memory: the day is the CAT date of a UTC instant, which is a time-zone
        // conversion rather than a translatable expression.
        return entries
            .GroupBy(entry => new DayKey(entry.UserId, AuditService.ToCAT(entry.CheckInTime).Date))
            .ToDictionary(
                group => group.Key,
                group => new VisitTotals(
                    group.First().Username,
                    // Distinct, because a rep who checks in twice at one shop made one call.
                    group.Select(entry => entry.CustomerCode)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count()));
    }

    /// <summary>
    /// The day's takings, from both places a van sale can land.
    ///
    /// A productive call is a distinct customer who bought, counted across both tables together — a
    /// shop that took an offline sale and an online invoice on the same day is one productive call,
    /// not two, and counting the tables separately would inflate the PCR.
    /// </summary>
    private async Task<Dictionary<DayKey, SaleTotals>> LoadSalesAsync(
        GetDepartureComplianceReportQuery query,
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken)
    {
        var sales = await VanSalesFactReader.LoadSalesAsync(
            db,
            new VanSalesFactFilter(from, to, query.UserId),
            cancellationToken);

        var grouped = new Dictionary<DayKey, SaleAccumulator>();

        foreach (var sale in sales)
        {
            var key = new DayKey(sale.UserId, sale.TradingDate);

            if (!grouped.TryGetValue(key, out var accumulator))
            {
                accumulator = new SaleAccumulator();
                grouped[key] = accumulator;
            }

            accumulator.Add(sale);
        }

        return grouped.ToDictionary(pair => pair.Key, pair => pair.Value.ToTotals());
    }

    /// <summary>
    /// Route customers first recorded on the day, by the rep who recorded them — the sheet's
    /// "New customers" line.
    /// </summary>
    private async Task<Dictionary<DayKey, int>> LoadNewCustomersAsync(
        DateTime windowStartUtc,
        DateTime windowEndUtc,
        CancellationToken cancellationToken)
    {
        var created = await db.RouteCustomers
            .AsNoTracking()
            .Where(customer => customer.CreatedByUserId != null
                               && customer.CreatedAt >= windowStartUtc
                               && customer.CreatedAt < windowEndUtc)
            .Select(customer => new { customer.CreatedByUserId, customer.CreatedAt })
            .ToListAsync(cancellationToken);

        return created
            .GroupBy(customer => new DayKey(
                customer.CreatedByUserId!.Value,
                AuditService.ToCAT(customer.CreatedAt).Date))
            .ToDictionary(group => group.Key, group => group.Count());
    }

    private async Task<Dictionary<Guid, UserName>> LoadUserNamesAsync(
        IEnumerable<Guid> userIds,
        CancellationToken cancellationToken)
    {
        var ids = userIds.ToList();

        if (ids.Count == 0)
        {
            return [];
        }

        var users = await db.Users
            .AsNoTracking()
            .Where(user => ids.Contains(user.Id))
            .Select(user => new { user.Id, user.Username, user.FirstName, user.LastName })
            .ToListAsync(cancellationToken);

        return users.ToDictionary(
            user => user.Id,
            user => new UserName(
                user.Username,
                string.IsNullOrWhiteSpace($"{user.FirstName} {user.LastName}".Trim())
                    ? null
                    : $"{user.FirstName} {user.LastName}".Trim()));
    }

    private readonly record struct DayKey(Guid UserId, DateTime TradingDate);

    private sealed record VisitTotals(string Username, int CustomersVisited);

    private sealed record UserName(string Username, string? FullName);

    private sealed record SaleTotals(
        int ProductiveCalls,
        decimal Cash,
        decimal Ecocash,
        decimal Innbucks,
        decimal Other,
        decimal Untendered,
        decimal Total,
        string? Currency);

    private sealed class SaleAccumulator
    {
        private readonly HashSet<string> _customers = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Sales with no route customer on them. Counted as one productive call between them rather
        /// than one each: they are the same unattributed bucket, and treating each as its own shop
        /// would make an unattributed day look like the busiest on the route.
        /// </summary>
        private bool _hasUnattributed;

        private decimal _cash;
        private decimal _ecocash;
        private decimal _innbucks;
        private decimal _other;
        private decimal _untendered;
        private decimal _total;
        private string? _currency;

        public void Add(VanSaleFact sale)
        {
            if (sale.RouteCustomerCode is null)
            {
                _hasUnattributed = true;
            }
            else
            {
                _customers.Add(sale.RouteCustomerCode);
            }

            _total += sale.TotalAmount;
            _currency ??= sale.Currency;

            switch (sale.Tender)
            {
                case VanSalesTender.Cash:
                    _cash += sale.TotalAmount;
                    break;
                case VanSalesTender.Ecocash:
                    _ecocash += sale.TotalAmount;
                    break;
                case VanSalesTender.Innbucks:
                    _innbucks += sale.TotalAmount;
                    break;
                // A swipe and a sale that named no tender are both outside the three declared
                // columns, and both are kept out of them — but they are counted apart, because only
                // the second is money the rep might have had in hand to declare. See
                // DepartureComplianceDayDto.DeclaredOverage.
                case VanSalesTender.Untendered:
                    _untendered += sale.TotalAmount;
                    break;
                default:
                    _other += sale.TotalAmount;
                    break;
            }
        }

        public SaleTotals ToTotals() => new(
            _customers.Count + (_hasUnattributed ? 1 : 0),
            _cash,
            _ecocash,
            _innbucks,
            _other,
            _untendered,
            _total,
            _currency);
    }
}
