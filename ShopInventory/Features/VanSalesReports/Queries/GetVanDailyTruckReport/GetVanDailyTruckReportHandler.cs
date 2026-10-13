using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesReports.Queries.GetVanDailyTruckReport;

public sealed class GetVanDailyTruckReportHandler(
    ApplicationDbContext db,
    IOptions<TaxSettings> tax
) : IRequestHandler<GetVanDailyTruckReportQuery, ErrorOr<VanDailyTruckReportResult>>
{
    /// <summary>
    /// A quarter. The report returns a row per route per working day, so a year of it is a few
    /// thousand rows that nobody reads as days; the performance report is the one for long periods.
    /// </summary>
    public const int MaximumDays = 93;

    public async Task<ErrorOr<VanDailyTruckReportResult>> Handle(
        GetVanDailyTruckReportQuery query,
        CancellationToken cancellationToken)
    {
        var from = query.FromDate.Date;
        var to = query.ToDate.Date;

        if (to < from)
        {
            return Errors.VanTruckDays.InvalidRange;
        }

        if ((to - from).TotalDays >= MaximumDays)
        {
            return Errors.VanTruckDays.RangeTooWide(MaximumDays);
        }

        var today = AuditService.ToCAT(DateTime.UtcNow).Date;
        var lastDay = to < today ? to : today;

        var workingDays = Days(from, to).Where(IsWorkingDay).ToList();
        var workingDaysElapsed = workingDays.Where(day => day <= today).ToList();

        var trucks = await VanTruckDays.LoadAsync(db, tax.Value, from, to, null, null, null, cancellationToken);

        // Everyone who could be on a truck: a login with a van account, a route, or both.
        var reps = await db.Users
            .AsNoTracking()
            .Where(user => user.RouteId != null || (user.AssignedBusinessPartnerCode != null && user.AssignedBusinessPartnerCode != ""))
            .Select(user => new { user.Id, user.RouteId, user.AssignedBusinessPartnerCode })
            .ToListAsync(cancellationToken);

        var accounts = reps.ToDictionary(rep => rep.Id, rep => rep.AssignedBusinessPartnerCode);

        // The route a truck runs when its day says nothing: the one most of its reps are assigned to.
        var routeOfTruck = reps
            .Where(rep => rep.RouteId.HasValue)
            .GroupBy(rep => VanTruckDays.TruckOf(rep.Id, accounts))
            .ToDictionary(
                truck => truck.Key,
                truck => truck
                    .GroupBy(rep => rep.RouteId!.Value)
                    .OrderByDescending(route => route.Count())
                    .ThenBy(route => route.Key)
                    .First().Key);

        var rows = new Dictionary<(int RouteId, DateTime Day), DayAccumulator>();
        var salesOutsideRoutes = 0m;

        DayAccumulator RowFor(int routeId, DateTime day)
        {
            if (!rows.TryGetValue((routeId, day), out var row))
            {
                row = new DayAccumulator();
                rows[(routeId, day)] = row;
            }

            return row;
        }

        foreach (var (key, truckDay) in trucks.All)
        {
            // The day's own record first: it is a snapshot of the route the truck was opened on,
            // and a truck moved to another route last week must not take its history with it.
            var routeId = truckDay.RouteDays.Select(day => day.RouteId).FirstOrDefault(id => id.HasValue)
                          ?? (routeOfTruck.TryGetValue(key.Truck, out var assigned) ? assigned : (int?)null);

            if (routeId is not { } route)
            {
                salesOutsideRoutes += truckDay.Sales.Where(IsDollars).Sum(sale => sale.TotalAmount);
                continue;
            }

            RowFor(route, key.TradingDate).Add(truckDay);
        }

        await AddNewCustomersAsync(from, to, accounts, routeOfTruck, trucks, RowFor, cancellationToken);

        var entries = await db.VanTruckDayEntries
            .AsNoTracking()
            .Where(entry => entry.TradingDate >= from && entry.TradingDate <= to)
            .Select(entry => new { entry.RouteId, entry.TradingDate, entry.AreaName, entry.FuelLitres, entry.AccountSales, entry.Notes })
            .ToListAsync(cancellationToken);

        foreach (var entry in entries)
        {
            var row = RowFor(entry.RouteId, entry.TradingDate.Date);
            row.OfficeArea = entry.AreaName;
            row.FuelLitres = entry.FuelLitres;
            row.AccountSales = entry.AccountSales;
            row.Notes = entry.Notes;
        }

        // Live routes are listed whether or not they traded: a truck that reported nothing all week
        // is the finding. A retired or deleted one is listed only where it has days in the period.
        var routeIds = rows.Keys.Select(key => key.RouteId).Distinct().ToList();

        var routes = await db.Routes
            .AsNoTracking()
            .Where(route => (route.IsActive && route.DeletedAt == null) || routeIds.Contains(route.Id))
            .Select(route => new RouteFacts(
                route.Id, route.Code, route.Name, route.Territory, route.OdometerUnit, route.DailySalesTarget,
                route.IsActive && route.DeletedAt == null))
            .ToListAsync(cancellationToken);

        var plan = await RouteDayPlan.LoadAsync(db, routes.Select(route => route.Id).ToList(), cancellationToken);

        var days = new List<VanDailyTruckDayDto>();

        foreach (var route in routes.OrderBy(route => route.Territory).ThenBy(route => route.Name))
        {
            var routeDays = rows.Keys.Where(key => key.RouteId == route.Id).Select(key => key.Day);

            // A live route owes a row for every working day so far. Any route owes one for a day it traded.
            var listed = (route.IsLive ? workingDaysElapsed : [])
                .Concat(routeDays)
                .Where(day => day >= from && day <= lastDay)
                .Distinct()
                .OrderBy(day => day);

            foreach (var day in listed)
            {
                rows.TryGetValue((route.Id, day), out var row);
                days.Add(Build(route, day, row ?? new DayAccumulator(), plan[route.Id], today));
            }
        }

        var summaries = routes
            .OrderBy(route => route.Territory)
            .ThenBy(route => route.Name)
            .Select(route => Summarise(
                route.Id,
                route.Code,
                route.Name,
                route.Territory,
                route.DailySalesTarget,
                days.Where(day => day.RouteId == route.Id).ToList(),
                workingDays.Count,
                workingDaysElapsed.Count))
            .ToList();

        var total = Summarise(
            null,
            "ALL",
            "All trucks",
            null,
            routes.Any(route => route.IsLive && route.DailySalesTarget.HasValue)
                ? routes.Where(route => route.IsLive).Sum(route => route.DailySalesTarget ?? 0m)
                : null,
            days,
            workingDays.Count,
            workingDaysElapsed.Count);

        return new VanDailyTruckReportResult(
            from,
            to,
            DateTime.UtcNow,
            workingDays.Count,
            workingDaysElapsed.Count,
            summaries,
            days.OrderBy(day => day.TradingDate).ThenBy(day => day.Territory).ThenBy(day => day.RouteName).ToList(),
            total,
            new VanDailyTruckExceptionsDto(
                days.Count(day => day.BelowTarget),
                days.Count(day => day.NotReported),
                days.Count(day => day.MileageNeedsReview),
                days.Count(day => day.DistanceWithoutSales),
                days.Count(day => day.ReturnedLate)),
            salesOutsideRoutes);
    }

    /// <summary>
    /// Route customers first recorded on each day, credited to the truck of the rep who recorded them.
    /// </summary>
    private async Task AddNewCustomersAsync(
        DateTime from,
        DateTime to,
        Dictionary<Guid, string?> accounts,
        Dictionary<string, int> routeOfTruck,
        VanTruckDays trucks,
        Func<int, DateTime, DayAccumulator> rowFor,
        CancellationToken cancellationToken)
    {
        var (windowStartUtc, windowEndUtc) = VanSalesFacts.ToUtcWindow(from, to);

        var created = await db.RouteCustomers
            .AsNoTracking()
            .Where(customer => customer.CreatedByUserId != null
                               && customer.CreatedAt >= windowStartUtc
                               && customer.CreatedAt < windowEndUtc)
            .Select(customer => new { customer.CreatedByUserId, customer.CreatedAt })
            .ToListAsync(cancellationToken);

        foreach (var customer in created)
        {
            var day = VanSalesFacts.TradingDayOf(customer.CreatedAt);
            var truck = VanTruckDays.TruckOf(customer.CreatedByUserId!.Value, accounts);

            var routeId = (trucks.All.TryGetValue(new VanTruckDayKey(truck, day), out var truckDay)
                              ? truckDay.RouteDays.Select(routeDay => routeDay.RouteId).FirstOrDefault(id => id.HasValue)
                              : null)
                          ?? (routeOfTruck.TryGetValue(truck, out var assigned) ? assigned : (int?)null);

            if (routeId is { } route)
            {
                rowFor(route, day).NewCustomers++;
            }
        }
    }

    private static VanDailyTruckDayDto Build(
        RouteFacts route,
        DateTime day,
        DayAccumulator row,
        IEnumerable<RouteDayPlan.Stop> stops,
        DateTime today)
    {
        var workingDay = IsWorkingDay(day);
        var dollars = row.Sales.Where(IsDollars).ToList();

        decimal Paid(VanSalesTender tender) =>
            dollars.Where(sale => sale.Tender == tender).Sum(sale => sale.TotalAmount);

        // The reading that covers the most road speaks for the truck: both reps may read the odometer,
        // and adding their readings would double the trip.
        var odometer = row.RouteDays
            .OrderByDescending(routeDay => routeDay.KilometresTravelled ?? -1)
            .ThenByDescending(routeDay => routeDay.StartingMileage.HasValue)
            .FirstOrDefault();

        var dayArea = row.RouteDays.Select(routeDay => routeDay.AreaName).FirstOrDefault(area => !string.IsNullOrWhiteSpace(area));
        var plannedArea = RouteDayPlan.AreaFor(stops, day.DayOfWeek);

        // What the office said wins, then what the day was opened with, then the plan for the weekday.
        string? area = null;
        string? areaSource = null;

        if (!string.IsNullOrWhiteSpace(row.OfficeArea))
        {
            (area, areaSource) = (row.OfficeArea, "Office");
        }
        else if (dayArea is not null)
        {
            (area, areaSource) = (dayArea, "Day");
        }
        else if (plannedArea is not null && workingDay)
        {
            (area, areaSource) = (plannedArea, "Plan");
        }

        var returns = row.RouteDays
            .Where(routeDay => routeDay.ReturnedAt.HasValue)
            .Select(routeDay => routeDay.ReturnedAt!.Value)
            .ToList();

        var timeOut = row.RouteDays.Count > 0
            ? AuditService.ToCAT(row.RouteDays.Min(routeDay => routeDay.DepartedAt))
            : (DateTime?)null;
        var timeIn = returns.Count > 0 ? AuditService.ToCAT(returns.Max()) : (DateTime?)null;

        var declared = row.RouteDays.Select(routeDay => routeDay.DeclaredTotal).Where(amount => amount.HasValue).ToList();
        var otherCurrencies = row.Sales.Where(sale => !IsDollars(sale)).Sum(sale => sale.TotalAmount);

        return new VanDailyTruckDayDto(
            RouteId: route.Id,
            RouteCode: route.Code,
            RouteName: route.Name,
            Territory: route.Territory,
            TradingDate: day,
            IsWorkingDay: workingDay,
            IsComplete: day < today,
            HasDayRecord: row.RouteDays.Count > 0,
            HasActivity: row.RouteDays.Count > 0 || row.Sales.Count > 0 || row.CustomersVisited > 0,
            AreaName: area,
            AreaSource: areaSource,
            TimeOut: timeOut,
            TimeIn: timeIn,
            ReturnedLate: timeIn is { } back && back.Date > day,
            PlannedCustomers: row.Planned,
            CustomersVisited: row.CustomersVisited,
            ProductiveCalls: row.ProductiveCalls,
            CallsMade: row.CallsMade,
            NewCustomers: row.NewCustomers,
            SalesCount: dollars.Count,
            Sales: dollars.Sum(sale => sale.TotalAmount),
            Cash: Paid(VanSalesTender.Cash),
            Ecocash: Paid(VanSalesTender.Ecocash),
            Innbucks: Paid(VanSalesTender.Innbucks),
            OtherTender: Paid(VanSalesTender.Other),
            Untendered: Paid(VanSalesTender.Untendered),
            OtherCurrencySales: otherCurrencies == 0m ? null : otherCurrencies,
            AccountSales: row.AccountSales,
            DeclaredTotal: declared.Count > 0 ? declared.Sum() : null,
            StartingMileage: odometer?.StartingMileage,
            ClosingMileage: odometer?.ClosingMileage,
            OdometerUnit: (odometer?.OdometerUnit ?? route.OdometerUnit).ToString(),
            Kilometres: odometer?.KilometresTravelled,
            FuelLitres: row.FuelLitres,
            DailySalesTarget: workingDay ? route.DailySalesTarget : null,
            Notes: row.Notes);
    }

    private static VanDailyTruckSummaryDto Summarise(
        int? routeId,
        string code,
        string name,
        string? territory,
        decimal? dailyTarget,
        IReadOnlyCollection<VanDailyTruckDayDto> days,
        int workingDays,
        int workingDaysElapsed)
    {
        var withPlan = days.Where(day => day.PlannedCustomers is > 0).ToList();
        var withCheckIns = days.Where(day => day.CustomersVisited > 0).ToList();

        return new VanDailyTruckSummaryDto(
            RouteId: routeId,
            RouteCode: code,
            RouteName: name,
            Territory: territory,
            DailySalesTarget: dailyTarget,
            PeriodTarget: dailyTarget * workingDays,
            TargetToDate: dailyTarget * workingDaysElapsed,
            Sales: days.Sum(day => day.Sales),
            Ecocash: days.Sum(day => day.Ecocash),
            AccountSales: days.Sum(day => day.AccountSales ?? 0m),
            SalesCount: days.Sum(day => day.SalesCount),
            DaysReported: days.Count(day => day.IsWorkingDay && day.HasActivity),
            WorkingDaysElapsed: routeId is null
                ? days.Count(day => day.IsWorkingDay)
                : workingDaysElapsed,
            CustomersVisited: days.Sum(day => day.CustomersVisited),
            ProductiveCalls: days.Sum(day => day.ProductiveCalls),
            NewCustomers: days.Sum(day => day.NewCustomers),
            PlannedCustomers: withPlan.Sum(day => day.PlannedCustomers!.Value),
            CcrCustomersVisited: withPlan.Sum(day => day.CustomersVisited),
            PcrProductiveCalls: withCheckIns.Sum(day => day.ProductiveCalls),
            PcrCallsMade: withCheckIns.Sum(day => day.CallsMade),
            Kilometres: days.Sum(day => day.Kilometres ?? 0),
            FuelLitres: days.Sum(day => day.FuelLitres ?? 0m));
    }

    private static IEnumerable<DateTime> Days(DateTime from, DateTime to)
    {
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            yield return day;
        }
    }

    /// <summary>Monday to Friday. The rounds do not run at the weekend, and a target is not owed for one.</summary>
    public static bool IsWorkingDay(DateTime day) =>
        day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

    /// <summary>A sale that names no currency is a dollar sale: it is what every van document defaults to.</summary>
    private static bool IsDollars(VanSaleFact sale) =>
        string.IsNullOrWhiteSpace(sale.Currency) || string.Equals(sale.Currency.Trim(), "USD", StringComparison.OrdinalIgnoreCase);

    private sealed record RouteFacts(
        int Id,
        string Code,
        string Name,
        string? Territory,
        OdometerUnit OdometerUnit,
        decimal? DailySalesTarget,
        bool IsLive);

    /// <summary>One route's day, gathered from the truck-days that ran it and the office's entry.</summary>
    private sealed class DayAccumulator
    {
        public List<VanSaleFact> Sales { get; } = [];

        public List<VanRouteDayEntity> RouteDays { get; } = [];

        public int CustomersVisited { get; private set; }

        public int ProductiveCalls { get; private set; }

        public int CallsMade { get; private set; }

        public int? Planned { get; private set; }

        public int NewCustomers { get; set; }

        public string? OfficeArea { get; set; }

        public decimal? FuelLitres { get; set; }

        public decimal? AccountSales { get; set; }

        public string? Notes { get; set; }

        public void Add(VanTruckDays.TruckDay truckDay)
        {
            Sales.AddRange(truckDay.Sales);
            RouteDays.AddRange(truckDay.RouteDays);
            CustomersVisited += truckDay.CheckedInto.Count;
            ProductiveCalls += truckDay.ProductiveCalls;
            CallsMade += truckDay.CallsMade;

            if (truckDay.Planned is { } planned)
            {
                Planned = (Planned ?? 0) + planned;
            }
        }
    }
}
