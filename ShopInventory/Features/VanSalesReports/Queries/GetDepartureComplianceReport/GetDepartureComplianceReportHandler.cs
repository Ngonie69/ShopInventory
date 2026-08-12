using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

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
/// does so silently.
/// </summary>
public sealed class GetDepartureComplianceReportHandler(
    ApplicationDbContext db
) : IRequestHandler<GetDepartureComplianceReportQuery, ErrorOr<DepartureComplianceReportResult>>
{
    /// <summary>A day that produced neither a departure record, a visit nor a sale is not a day.</summary>
    private const int MaximumDays = 400;

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
        var windowStartUtc = AuditService.FromCAT(from);
        var windowEndUtc = AuditService.FromCAT(to.AddDays(1));

        var days = await LoadDaysAsync(query, from, to, cancellationToken);
        var visits = await LoadVisitsAsync(query, windowStartUtc, windowEndUtc, cancellationToken);
        var sales = await LoadSalesAsync(query, from, to, windowStartUtc, windowEndUtc, cancellationToken);
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

            rows.Add(BuildRow(key, day, visit, sale, newCustomerCount, name));
        }

        var ordered = rows
            .OrderByDescending(row => row.TradingDate)
            .ThenBy(row => row.FullName ?? row.Username, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new DepartureComplianceReportResult(from, to, ordered, Summarise(ordered));
    }

    private static DepartureComplianceDayDto BuildRow(
        DayKey key,
        VanRouteDayEntity? day,
        VisitTotals? visit,
        SaleTotals? sale,
        int newCustomerCount,
        UserName? name)
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
            Notes: day?.Notes);
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
        DateTime windowStartUtc,
        DateTime windowEndUtc,
        CancellationToken cancellationToken)
    {
        var desktopQuery = db.DesktopSales
            .AsNoTracking()
            .Where(sale => sale.SourceSystem == SaleSourceSystems.VanSales
                           && sale.DocDate >= from
                           && sale.DocDate <= to
                           && sale.CreatedBy != null);

        var desktopSales = await desktopQuery
            .Select(sale => new SaleRow(
                sale.CreatedBy!,
                sale.DocDate,
                sale.RouteCustomerCode,
                sale.PaymentMethod,
                sale.TotalAmount,
                sale.Currency))
            .ToListAsync(cancellationToken);

        // The online path posts straight to SAP and writes no DesktopSale; the confirmed reservation
        // is its only local record. Pending, cancelled and expired reservations are not sales.
        var reservations = await db.StockReservations
            .AsNoTracking()
            .Where(reservation => reservation.SourceSystem == SaleSourceSystems.VanSales
                                  && reservation.Status == ReservationStatus.Confirmed
                                  && reservation.CreatedAt >= windowStartUtc
                                  && reservation.CreatedAt < windowEndUtc
                                  && reservation.CreatedBy != null)
            .Select(reservation => new
            {
                reservation.CreatedBy,
                reservation.CreatedAt,
                reservation.RouteCustomerCode,
                reservation.TotalValue,
                reservation.Currency
            })
            .ToListAsync(cancellationToken);

        var reservationRows = reservations.Select(reservation => new SaleRow(
            reservation.CreatedBy!,
            AuditService.ToCAT(reservation.CreatedAt).Date,
            reservation.RouteCustomerCode,
            // No payment column on a reservation, so an online sale cannot be attributed to a tender.
            // Reported as unallocated rather than silently counted as cash.
            null,
            reservation.TotalValue,
            reservation.Currency));

        var rows = desktopSales.Concat(reservationRows).ToList();

        var grouped = new Dictionary<DayKey, SaleAccumulator>();

        foreach (var row in rows)
        {
            // CreatedBy holds the user's id as a string on both tables. Anything else is a sale from
            // another system that slipped past the source filter, and is skipped rather than guessed.
            if (!Guid.TryParse(row.CreatedBy, out var userId))
            {
                continue;
            }

            if (query.UserId.HasValue && userId != query.UserId.Value)
            {
                continue;
            }

            var key = new DayKey(userId, row.TradingDate);

            if (!grouped.TryGetValue(key, out var accumulator))
            {
                accumulator = new SaleAccumulator();
                grouped[key] = accumulator;
            }

            accumulator.Add(row);
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

    private sealed record SaleRow(
        string CreatedBy,
        DateTime TradingDate,
        string? RouteCustomerCode,
        string? PaymentMethod,
        decimal Amount,
        string? Currency);

    private sealed record SaleTotals(
        int ProductiveCalls,
        decimal Cash,
        decimal Ecocash,
        decimal Innbucks,
        decimal Other,
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
        private decimal _total;
        private string? _currency;

        public void Add(SaleRow row)
        {
            if (string.IsNullOrWhiteSpace(row.RouteCustomerCode))
            {
                _hasUnattributed = true;
            }
            else
            {
                _customers.Add(row.RouteCustomerCode.Trim());
            }

            _total += row.Amount;
            _currency ??= row.Currency;

            switch (NormalisePaymentMethod(row.PaymentMethod))
            {
                case PaymentBucket.Cash:
                    _cash += row.Amount;
                    break;
                case PaymentBucket.Ecocash:
                    _ecocash += row.Amount;
                    break;
                case PaymentBucket.Innbucks:
                    _innbucks += row.Amount;
                    break;
                default:
                    _other += row.Amount;
                    break;
            }
        }

        public SaleTotals ToTotals() => new(
            _customers.Count + (_hasUnattributed ? 1 : 0),
            _cash,
            _ecocash,
            _innbucks,
            _other,
            _total,
            _currency);

        /// <summary>
        /// Which column of the sheet a tender belongs in.
        ///
        /// Matches on the brand the handset sends rather than on a fiscal money type, because ZIMRA
        /// has no notion of Ecocash or Innbucks — both are <c>MobileWallet</c> on the receipt, and
        /// splitting them is exactly what this report is for. Older handsets send nothing at all,
        /// which lands in Other rather than being assumed to be cash.
        /// </summary>
        private static PaymentBucket NormalisePaymentMethod(string? paymentMethod)
        {
            if (string.IsNullOrWhiteSpace(paymentMethod))
            {
                return PaymentBucket.Other;
            }

            var normalised = paymentMethod.Trim();

            if (normalised.Equals("Cash", StringComparison.OrdinalIgnoreCase))
            {
                return PaymentBucket.Cash;
            }

            if (normalised.Contains("ecocash", StringComparison.OrdinalIgnoreCase))
            {
                return PaymentBucket.Ecocash;
            }

            return normalised.Contains("innbucks", StringComparison.OrdinalIgnoreCase)
                   || normalised.Contains("inbucks", StringComparison.OrdinalIgnoreCase)
                ? PaymentBucket.Innbucks
                : PaymentBucket.Other;
        }

        private enum PaymentBucket
        {
            Cash,
            Ecocash,
            Innbucks,
            Other
        }
    }
}
