using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Features.RouteCustomers.Queries;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.VanSalesReports.Queries.GetVanSalesPerformanceReport;

/// <summary>
/// Builds the van sales performance report: what sold, cut by route, rep, item and time.
/// </summary>
/// <remarks>
/// Everything here reads through <see cref="VanSalesFactReader"/> rather than the sale tables, so this
/// report and the compliance report cannot disagree about a day's takings. The two figures that must
/// reconcile between them are the gross total and the productive-call count, and both are computed
/// here by the same rules the compliance handler uses.
///
/// Three joins are done in memory rather than in SQL, and each for the same reason: the online half of
/// a van sale keeps its date as a UTC instant, and the CAT trading day it belongs to is a time-zone
/// conversion, which is not a translatable expression. The facts are materialised by the reader
/// anyway, so the joins are dictionary hits over data already in the process.
/// </remarks>
public sealed class GetVanSalesPerformanceReportHandler(
    ApplicationDbContext db
) : IRequestHandler<GetVanSalesPerformanceReportQuery, ErrorOr<VanSalesPerformanceReportResult>>
{
    public async Task<ErrorOr<VanSalesPerformanceReportResult>> Handle(
        GetVanSalesPerformanceReportQuery query,
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

        if ((to - from).TotalDays > VanSalesFacts.MaximumDays)
        {
            return Error.Validation(
                "VanSalesReports.RangeTooWide",
                $"Choose a period of {VanSalesFacts.MaximumDays} days or fewer.");
        }

        var filter = new VanSalesFactFilter(from, to, query.UserId);

        var sales = await VanSalesFactReader.LoadSalesAsync(db, filter, cancellationToken);
        var lines = await VanSalesFactReader.LoadSaleLinesAsync(db, filter, cancellationToken);
        var days = await LoadRouteDaysAsync(query, from, to, cancellationToken);
        var visits = await LoadVisitsAsync(query, from, to, cancellationToken);
        var newOutlets = await LoadNewOutletsAsync(from, to, cancellationToken);

        // A route filter must not be satisfied by a sale that cannot be shown to belong to the route.
        // Same rule, and the same reason, as the compliance report: nothing on a sale whose rep never
        // opened a day says which route it was made on.
        if (!string.IsNullOrWhiteSpace(query.RouteCode))
        {
            var routeCode = query.RouteCode.Trim();

            bool OnRoute(VanSalesDayKey key) =>
                days.TryGetValue(key, out var day)
                && string.Equals(day.RouteCode, routeCode, StringComparison.OrdinalIgnoreCase);

            sales = sales.Where(sale => OnRoute(sale.Key)).ToList();
            lines = lines.Where(line => OnRoute(line.Key)).ToList();
        }

        var names = await LoadUserNamesAsync(
            sales.Select(sale => sale.UserId).Distinct(),
            cancellationToken);

        var routes = BuildRoutes(sales, days, visits);
        var priorLines = await LoadPriorLinesAsync(query, from, to, days, cancellationToken);

        var result = new VanSalesPerformanceReportResult(
            FromDate: from,
            ToDate: to,
            Summary: BuildSummary(sales, lines, days, visits, routes, newOutlets),
            Territories: BuildTerritories(routes),
            Routes: routes,
            Reps: BuildReps(sales, lines, days, visits, newOutlets, names),
            Items: BuildItems(lines, query.TopItems),
            LapsedItems: BuildLapsedItems(lines, priorLines, to),
            Trend: BuildTrend(sales, from, to),
            ItemPrices: BuildItemPrices(lines, names, query.TopItems),
            DropSizes: BuildDropSizes(sales),
            Coverage: BuildCoverage(sales, lines, days, visits));

        return result;
    }

    // ── Reads ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// The departure records for the period, which are the only thing that says what route a sale was
    /// made on. The unique index on (UserId, TradingDate) is what makes this dictionary safe.
    /// </summary>
    private async Task<Dictionary<VanSalesDayKey, VanRouteDayEntity>> LoadRouteDaysAsync(
        GetVanSalesPerformanceReportQuery query,
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

        var days = await queryable.ToListAsync(cancellationToken);

        return days.ToDictionary(day => new VanSalesDayKey(day.UserId, day.TradingDate));
    }

    /// <summary>
    /// The calls made, per rep per trading day.
    /// </summary>
    /// <remarks>
    /// The channel is pinned to van sales and is never a parameter — a query that takes a channel is a
    /// query that belongs in the merchandiser feature folder instead.
    ///
    /// A visit with no check-out is still a call, so nothing here requires one.
    /// </remarks>
    private async Task<Dictionary<VanSalesDayKey, HashSet<string>>> LoadVisitsAsync(
        GetVanSalesPerformanceReportQuery query,
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken)
    {
        var (windowStartUtc, windowEndUtc) = VanSalesFacts.ToUtcWindow(from, to);

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
            .Select(entry => new { entry.UserId, entry.CheckInTime, entry.CustomerCode })
            .ToListAsync(cancellationToken);

        return entries
            .GroupBy(entry => new VanSalesDayKey(entry.UserId, VanSalesFacts.TradingDayOf(entry.CheckInTime)))
            .ToDictionary(
                group => group.Key,
                // A rep who checks in twice at one shop made one call.
                group => group.Select(entry => entry.CustomerCode)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Shops first recorded in the period, by the rep who recorded them.</summary>
    private async Task<Dictionary<Guid, HashSet<string>>> LoadNewOutletsAsync(
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken)
    {
        var (windowStartUtc, windowEndUtc) = VanSalesFacts.ToUtcWindow(from, to);

        var created = await db.RouteCustomers
            .AsNoTracking()
            .Where(customer => customer.CreatedByUserId != null
                               && customer.CreatedAt >= windowStartUtc
                               && customer.CreatedAt < windowEndUtc)
            .Select(customer => new { customer.CreatedByUserId, customer.Code })
            .ToListAsync(cancellationToken);

        return created
            .GroupBy(customer => customer.CreatedByUserId!.Value)
            .ToDictionary(
                group => group.Key,
                group => group.Select(customer => customer.Code).ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The preceding window of equal length, read only to find what has stopped selling.
    ///
    /// Equal length matters: comparing a fortnight against a quarter would call half the catalogue
    /// lapsed.
    /// </summary>
    private async Task<List<VanSaleLineFact>> LoadPriorLinesAsync(
        GetVanSalesPerformanceReportQuery query,
        DateTime from,
        DateTime to,
        Dictionary<VanSalesDayKey, VanRouteDayEntity> days,
        CancellationToken cancellationToken)
    {
        var length = (to - from).Days + 1;
        var priorTo = from.AddDays(-1);
        var priorFrom = priorTo.AddDays(-(length - 1));

        var priorLines = await VanSalesFactReader.LoadSaleLinesAsync(
            db,
            new VanSalesFactFilter(priorFrom, priorTo, query.UserId),
            cancellationToken);

        if (string.IsNullOrWhiteSpace(query.RouteCode))
        {
            return priorLines;
        }

        // The prior window needs its own departure records; the ones already loaded cover this period.
        var routeCode = query.RouteCode.Trim();

        var priorDays = await db.VanRouteDays
            .AsNoTracking()
            .Where(day => day.TradingDate >= priorFrom
                          && day.TradingDate <= priorTo
                          && day.RouteCode == routeCode)
            .Select(day => new { day.UserId, day.TradingDate })
            .ToListAsync(cancellationToken);

        var keys = priorDays
            .Select(day => new VanSalesDayKey(day.UserId, day.TradingDate))
            .ToHashSet();

        return priorLines.Where(line => keys.Contains(line.Key)).ToList();
    }

    private async Task<Dictionary<Guid, VanSalesMeasures.UserName>> LoadUserNamesAsync(
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
            user => new VanSalesMeasures.UserName(
                user.Username,
                string.IsNullOrWhiteSpace($"{user.FirstName} {user.LastName}".Trim())
                    ? null
                    : $"{user.FirstName} {user.LastName}".Trim()));
    }

    // ── Routes and territories ──────────────────────────────────────────────────

    private static List<VanSalesRouteResult> BuildRoutes(
        List<VanSaleFact> sales,
        Dictionary<VanSalesDayKey, VanRouteDayEntity> days,
        Dictionary<VanSalesDayKey, HashSet<string>> visits)
    {
        var grouped = sales
            .GroupBy(sale => VanSalesMeasures.RouteKeyOf(sale.Key, days))
            .Select(group =>
            {
                var key = group.Key;
                var dayKeys = group.Select(sale => sale.Key).Distinct().ToList();
                var dayRecords = dayKeys
                    .Select(dayKey => days.TryGetValue(dayKey, out var day) ? day : null)
                    .Where(day => day is not null)
                    .Select(day => day!)
                    .ToList();

                return new VanSalesRouteResult(
                    HasRouteDay: key.HasRouteDay,
                    RouteCode: key.RouteCode,
                    RouteName: key.RouteName,
                    Territory: key.Territory,
                    RepCount: group.Select(sale => sale.UserId).Distinct().Count(),
                    TradingDayCount: dayKeys.Count,
                    PlannedCalls: dayRecords.Count > 0 ? dayRecords.Sum(day => day.PlannedCustomerCount) : null,
                    Calls: VanSalesMeasures.CountCalls(dayKeys, visits),
                    ProductiveCalls: VanSalesMeasures.CountProductiveCalls(group),
                    CustomerCount: VanSalesMeasures.CountOutletsThatBought(group),
                    KilometresTravelled: VanSalesMeasures.SumKilometres(dayRecords),
                    TotalsByCurrency: VanSalesMeasures.MoneyByCurrency(group));
            })
            .ToList();

        // Real routes first, biggest trade first. The no-departure-record bucket sorts last on purpose:
        // it is a gap, not a route, and placing it among them by value invites it to be read as one.
        return grouped
            .OrderBy(route => route.HasRouteDay ? 0 : 1)
            .ThenByDescending(route => route.TotalsByCurrency.Sum(total => total.DocumentCount))
            .ThenBy(route => route.RouteCode, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<VanSalesTerritoryResult> BuildTerritories(List<VanSalesRouteResult> routes) =>
        routes
            .Where(route => route.HasRouteDay)
            .GroupBy(route => route.Territory, StringComparer.OrdinalIgnoreCase)
            .Select(group => new VanSalesTerritoryResult(
                Territory: group.Key,
                RouteCount: group.Count(),
                // Reps and customers cannot simply be added across routes, and are not: this is the
                // best available roll-up from route rows, and it deliberately sums rather than
                // pretending to a distinct count it no longer has the rows to take.
                RepCount: group.Sum(route => route.RepCount),
                TradingDayCount: group.Sum(route => route.TradingDayCount),
                ProductiveCalls: group.Sum(route => route.ProductiveCalls),
                CustomerCount: group.Sum(route => route.CustomerCount),
                TotalsByCurrency: VanSalesMeasures.FoldMoney(group.SelectMany(route => route.TotalsByCurrency))))
            .OrderByDescending(territory => territory.TotalsByCurrency.Sum(total => total.DocumentCount))
            .ThenBy(territory => territory.Territory, StringComparer.OrdinalIgnoreCase)
            .ToList();

    // ── Reps ────────────────────────────────────────────────────────────────────

    private static List<VanSalesRepResult> BuildReps(
        List<VanSaleFact> sales,
        List<VanSaleLineFact> lines,
        Dictionary<VanSalesDayKey, VanRouteDayEntity> days,
        Dictionary<VanSalesDayKey, HashSet<string>> visits,
        Dictionary<Guid, HashSet<string>> newOutlets,
        Dictionary<Guid, VanSalesMeasures.UserName> names)
    {
        var itemsByRep = lines
            .GroupBy(line => line.UserId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(line => line.ItemCode)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                    .Count);

        return sales
            .GroupBy(sale => sale.UserId)
            .Select(group =>
            {
                var userId = group.Key;
                var dayKeys = group.Select(sale => sale.Key).Distinct().ToList();
                var dayRecords = dayKeys
                    .Select(dayKey => days.TryGetValue(dayKey, out var day) ? day : null)
                    .Where(day => day is not null)
                    .Select(day => day!)
                    .ToList();

                var bought = group
                    .Where(sale => sale.RouteCustomerCode is not null)
                    .Select(sale => sale.RouteCustomerCode!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                newOutlets.TryGetValue(userId, out var captured);
                names.TryGetValue(userId, out var name);

                return new VanSalesRepResult(
                    UserId: userId,
                    Username: name?.Username ?? userId.ToString(),
                    FullName: name?.FullName,
                    Routes: dayRecords
                        .Select(day => day.RouteCode)
                        .Where(code => !string.IsNullOrWhiteSpace(code))
                        .Select(code => code!)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    TradingDayCount: dayKeys.Count,
                    Calls: VanSalesMeasures.CountCalls(dayKeys, visits),
                    OutletsVisited: VanSalesMeasures.CountOutletsVisited(dayKeys, visits),
                    ProductiveCalls: VanSalesMeasures.CountProductiveCalls(group),
                    CustomerCount: bought.Count,
                    NewOutlets: captured?.Count ?? 0,
                    // Captured and converted are different achievements, so the intersection is its own
                    // figure rather than a redefinition of the first.
                    NewOutletsWhoBought: captured is null ? 0 : captured.Count(bought.Contains),
                    ItemCount: itemsByRep.TryGetValue(userId, out var itemCount) ? itemCount : 0,
                    KilometresTravelled: VanSalesMeasures.SumKilometres(dayRecords),
                    TotalsByCurrency: VanSalesMeasures.MoneyByCurrency(group));
            })
            .OrderByDescending(rep => rep.TotalsByCurrency.Sum(total => total.DocumentCount))
            .ThenBy(rep => rep.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ── Items ───────────────────────────────────────────────────────────────────

    private static List<VanSalesItemResult> BuildItems(List<VanSaleLineFact> lines, int topItems)
    {
        var ranked = lines
            .GroupBy(line => line.ItemCode, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                ItemCode = group.Key,
                Description = VanSalesMeasures.FirstDescription(group),
                LineCount = group.Count(),
                DocumentCount = group.Select(line => line.ExternalReferenceId)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                CustomerCount = group.Where(line => line.RouteCustomerCode is not null)
                    .Select(line => line.RouteCustomerCode!)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                RepCount = group.Select(line => line.UserId).Distinct().Count(),
                TradingDayCount = group.Select(line => line.TradingDate).Distinct().Count(),
                FirstSoldOn = group.Min(line => line.TradingDate),
                LastSoldOn = group.Max(line => line.TradingDate),
                Quantities = VanSalesMeasures.QuantitiesByUoM(group),
                Totals = VanSalesMeasures.LineMoneyByCurrency(group)
            })
            // Ranked on reach, not on value or quantity. Value cannot be compared across currencies and
            // quantity cannot be compared across units, so either would rank on an accident of the mix.
            .OrderByDescending(item => item.LineCount)
            .ThenByDescending(item => item.CustomerCount)
            .ThenBy(item => item.ItemCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var capped = topItems > 0 ? ranked.Take(topItems) : ranked;

        return capped
            .Select((item, index) => new VanSalesItemResult(
                Rank: index + 1,
                ItemCode: item.ItemCode,
                ItemDescription: item.Description,
                LineCount: item.LineCount,
                DocumentCount: item.DocumentCount,
                CustomerCount: item.CustomerCount,
                RepCount: item.RepCount,
                TradingDayCount: item.TradingDayCount,
                FirstSoldOn: item.FirstSoldOn,
                LastSoldOn: item.LastSoldOn,
                QuantitiesByUoM: item.Quantities,
                TotalsByCurrency: item.Totals))
            .ToList();
    }

    private static List<VanSalesLapsedItemResult> BuildLapsedItems(
        List<VanSaleLineFact> lines,
        List<VanSaleLineFact> priorLines,
        DateTime to)
    {
        var sold = lines.Select(line => line.ItemCode).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return priorLines
            .Where(line => !sold.Contains(line.ItemCode))
            .GroupBy(line => line.ItemCode, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var lastSoldOn = group.Max(line => line.TradingDate);

                return new VanSalesLapsedItemResult(
                    ItemCode: group.Key,
                    ItemDescription: VanSalesMeasures.FirstDescription(group),
                    LastSoldOn: lastSoldOn,
                    DaysSinceLastSale: Math.Max(0, (int)(to - lastSoldOn).TotalDays),
                    PriorLineCount: group.Count(),
                    PriorCustomerCount: group.Where(line => line.RouteCustomerCode is not null)
                        .Select(line => line.RouteCustomerCode!)
                        .Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    PriorTotalsByCurrency: VanSalesMeasures.LineMoneyByCurrency(group));
            })
            .OrderByDescending(item => item.PriorLineCount)
            .ThenBy(item => item.ItemCode, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ── Trend ───────────────────────────────────────────────────────────────────

    private static VanSalesTrendResult BuildTrend(List<VanSaleFact> sales, DateTime from, DateTime to)
    {
        var byDay = sales
            .GroupBy(sale => sale.TradingDate)
            .ToDictionary(group => group.Key, group => group.ToList());

        var daily = new List<VanSalesTrendPointResult>();

        // Every calendar day, including the silent ones. A day with no trade is a finding, and leaving
        // it out makes the spacing of the curve say something untrue about the days either side.
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            byDay.TryGetValue(date, out var dayFacts);
            var facts = dayFacts ?? [];

            daily.Add(new VanSalesTrendPointResult(
                TradingDate: date,
                DayOfWeek: date.DayOfWeek,
                RepsTrading: facts.Select(sale => sale.UserId).Distinct().Count(),
                ProductiveCalls: VanSalesMeasures.CountProductiveCalls(facts),
                DocumentCount: facts.Count,
                TotalsByCurrency: VanSalesMeasures.MoneyByCurrency(facts)));
        }

        return new VanSalesTrendResult(
            Daily: daily,
            DayOfWeek: BuildDayOfWeek(daily),
            Monthly: BuildMonthly(daily, from, to));
    }

    private static List<VanSalesSeasonPointResult> BuildDayOfWeek(List<VanSalesTrendPointResult> daily)
    {
        var order = new[]
        {
            System.DayOfWeek.Monday, System.DayOfWeek.Tuesday, System.DayOfWeek.Wednesday,
            System.DayOfWeek.Thursday, System.DayOfWeek.Friday, System.DayOfWeek.Saturday,
            System.DayOfWeek.Sunday
        };

        return order
            .Select(weekday =>
            {
                var points = daily.Where(point => point.DayOfWeek == weekday).ToList();

                return new VanSalesSeasonPointResult(
                    Label: weekday.ToString(),
                    Year: null,
                    Month: null,
                    DayOfWeek: weekday,
                    IsPartial: false,
                    // Averages divide by the calendar count, not the active one. Dividing by days that
                    // traded makes a weekday nobody works look like an average one.
                    CalendarDayCount: points.Count,
                    ActiveDayCount: points.Count(point => point.DocumentCount > 0),
                    DocumentCount: points.Sum(point => point.DocumentCount),
                    ProductiveCalls: points.Sum(point => point.ProductiveCalls),
                    TotalsByCurrency: VanSalesMeasures.FoldMoney(points.SelectMany(point => point.TotalsByCurrency)));
            })
            .ToList();
    }

    private static List<VanSalesSeasonPointResult> BuildMonthly(
        List<VanSalesTrendPointResult> daily,
        DateTime from,
        DateTime to) =>
        daily
            .GroupBy(point => new { point.TradingDate.Year, point.TradingDate.Month })
            .OrderBy(group => group.Key.Year)
            .ThenBy(group => group.Key.Month)
            .Select(group =>
            {
                var first = new DateTime(group.Key.Year, group.Key.Month, 1);
                var last = first.AddMonths(1).AddDays(-1);

                return new VanSalesSeasonPointResult(
                    Label: first.ToString("MMM yyyy"),
                    Year: group.Key.Year,
                    Month: group.Key.Month,
                    DayOfWeek: null,
                    // A month the window only half covers must say so, or its short total reads as a
                    // collapse in trade rather than as a clipped period.
                    IsPartial: first < from || last > to,
                    CalendarDayCount: group.Count(),
                    ActiveDayCount: group.Count(point => point.DocumentCount > 0),
                    DocumentCount: group.Sum(point => point.DocumentCount),
                    ProductiveCalls: group.Sum(point => point.ProductiveCalls),
                    TotalsByCurrency: VanSalesMeasures.FoldMoney(group.SelectMany(point => point.TotalsByCurrency)));
            })
            .ToList();

    // ── Price realisation ───────────────────────────────────────────────────────

    private static List<VanSalesItemPriceResult> BuildItemPrices(
        List<VanSaleLineFact> lines,
        Dictionary<Guid, VanSalesMeasures.UserName> names,
        int topItems)
    {
        // A zero-quantity line has no achieved price. Counted in coverage, never divided by.
        var priced = lines.Where(line => line.Quantity != 0).ToList();

        var results = priced
            // One item, one currency and one unit: the only grouping in which a weighted average price
            // means anything at all.
            .GroupBy(line => new
            {
                ItemCode = line.ItemCode.ToUpperInvariant(),
                Currency = RouteCustomerSalesReporting.NormalizeCurrency(line.Currency),
                line.UoMCode
            })
            .Select(group =>
            {
                var quantity = group.Sum(line => line.Quantity);
                var gross = group.Sum(line => line.LineTotal);
                var weighted = quantity == 0 ? 0m : decimal.Round(gross / quantity, 4);
                var unitPrices = group.Select(AchievedPrice).ToList();

                return new VanSalesItemPriceResult(
                    ItemCode: group.Key.ItemCode,
                    ItemDescription: VanSalesMeasures.FirstDescription(group),
                    Currency: group.Key.Currency,
                    UoMCode: group.Key.UoMCode,
                    LineCount: group.Count(),
                    Quantity: quantity,
                    Gross: gross,
                    WeightedAveragePrice: weighted,
                    MinUnitPrice: unitPrices.Min(),
                    MaxUnitPrice: unitPrices.Max(),
                    Reps: BuildRepPrices(group, weighted, names));
            })
            // Widest spread first: that is the row a manager can act on.
            .OrderByDescending(item => item.PriceSpreadPercent ?? 0m)
            .ThenByDescending(item => item.LineCount)
            .ThenBy(item => item.ItemCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return topItems > 0 ? results.Take(topItems).ToList() : results;
    }

    private static List<VanSalesRepPriceResult> BuildRepPrices(
        IEnumerable<VanSaleLineFact> lines,
        decimal itemWeightedAverage,
        Dictionary<Guid, VanSalesMeasures.UserName> names) =>
        lines
            .GroupBy(line => line.UserId)
            .Select(group =>
            {
                var quantity = group.Sum(line => line.Quantity);
                var gross = group.Sum(line => line.LineTotal);
                var weighted = quantity == 0 ? 0m : decimal.Round(gross / quantity, 4);

                names.TryGetValue(group.Key, out var name);

                return new VanSalesRepPriceResult(
                    UserId: group.Key,
                    Username: name?.Username ?? group.Key.ToString(),
                    FullName: name?.FullName,
                    LineCount: group.Count(),
                    Quantity: quantity,
                    Gross: gross,
                    WeightedAveragePrice: weighted,
                    VarianceFromItemPercent: itemWeightedAverage == 0
                        ? null
                        : decimal.Round((weighted - itemWeightedAverage) / itemWeightedAverage * 100m, 2));
            })
            .OrderBy(rep => rep.WeightedAveragePrice)
            .ToList();

    /// <summary>
    /// What the line actually fetched per unit — the line total over the quantity, not the unit price
    /// column. The two disagree once per-line rounding lands, and the total is what was paid.
    /// </summary>
    private static decimal AchievedPrice(VanSaleLineFact line) =>
        line.Quantity == 0 ? 0m : decimal.Round(line.LineTotal / line.Quantity, 4);

    // ── Drop size ───────────────────────────────────────────────────────────────

    private static List<VanSalesDropSizeResult> BuildDropSizes(List<VanSaleFact> sales) =>
        sales
            .GroupBy(sale => RouteCustomerSalesReporting.NormalizeCurrency(sale.Currency))
            .Select(currencyGroup =>
            {
                var drops = currencyGroup
                    .GroupBy(VanSalesMeasures.DropKey)
                    .Select(drop => drop.Sum(sale => sale.TotalAmount))
                    .OrderBy(value => value)
                    .ToList();

                var total = drops.Sum();

                return new VanSalesDropSizeResult(
                    Currency: currencyGroup.Key,
                    DropCount: drops.Count,
                    Total: total,
                    Minimum: drops[0],
                    P25: VanSalesMeasures.Percentile(drops, 0.25),
                    Median: VanSalesMeasures.Percentile(drops, 0.50),
                    P75: VanSalesMeasures.Percentile(drops, 0.75),
                    Maximum: drops[^1],
                    Mean: decimal.Round(total / drops.Count, 2),
                    Buckets: BuildBuckets(drops, currencyGroup.Key));
            })
            .OrderByDescending(distribution => distribution.Total)
            .ThenBy(distribution => distribution.Currency, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Fixed bands, not quantiles of this period's own data. A band that moves with the data compares
    /// nothing to nothing — the whole use of the distribution is holding two periods against each
    /// other.
    /// </summary>
    private static List<VanSalesDropSizeBucketResult> BuildBuckets(List<decimal> drops, string currency)
    {
        var edges = string.Equals(currency, "USD", StringComparison.OrdinalIgnoreCase)
            ? new decimal[] { 0m, 5m, 20m, 50m, 100m }
            : [0m, 100m, 500m, 2000m, 5000m];

        var total = drops.Sum();
        var buckets = new List<VanSalesDropSizeBucketResult>(edges.Length);

        for (var index = 0; index < edges.Length; index++)
        {
            var lower = edges[index];
            decimal? upper = index + 1 < edges.Length ? edges[index + 1] : null;

            var inBand = drops
                .Where(value => value >= lower && (upper is null || value < upper.Value))
                .ToList();

            var bandTotal = inBand.Sum();

            buckets.Add(new VanSalesDropSizeBucketResult(
                Label: upper is null ? $"{lower:N0}+" : $"{lower:N0}–{upper.Value:N0}",
                LowerBound: lower,
                UpperBound: upper,
                DropCount: inBand.Count,
                Total: bandTotal)
            {
                SharePercent = total == 0 ? null : (double)decimal.Round(bandTotal / total * 100m, 2)
            });
        }

        return buckets;
    }

    // ── Summary and coverage ────────────────────────────────────────────────────

    private static VanSalesPerformanceSummaryResult BuildSummary(
        List<VanSaleFact> sales,
        List<VanSaleLineFact> lines,
        Dictionary<VanSalesDayKey, VanRouteDayEntity> days,
        Dictionary<VanSalesDayKey, HashSet<string>> visits,
        List<VanSalesRouteResult> routes,
        Dictionary<Guid, HashSet<string>> newOutlets)
    {
        var dayKeys = sales.Select(sale => sale.Key).Distinct().ToList();
        var dayRecords = dayKeys
            .Select(dayKey => days.TryGetValue(dayKey, out var day) ? day : null)
            .Where(day => day is not null)
            .Select(day => day!)
            .ToList();

        var reps = sales.Select(sale => sale.UserId).ToHashSet();

        return new VanSalesPerformanceSummaryResult(
            RepCount: reps.Count,
            RouteCount: routes.Count(route => route.HasRouteDay && route.RouteCode is not null),
            TerritoryCount: routes
                .Where(route => route.HasRouteDay && route.Territory is not null)
                .Select(route => route.Territory!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(),
            TradingDayCount: dayKeys.Count,
            DocumentCount: sales.Count,
            Calls: VanSalesMeasures.CountCalls(dayKeys, visits),
            ProductiveCalls: VanSalesMeasures.CountProductiveCalls(sales),
            CustomerCount: VanSalesMeasures.CountOutletsThatBought(sales),
            ItemCount: lines.Select(line => line.ItemCode)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            NewOutlets: newOutlets
                .Where(pair => reps.Contains(pair.Key))
                .Sum(pair => pair.Value.Count),
            KilometresTravelled: VanSalesMeasures.SumKilometres(dayRecords),
            TotalsByCurrency: VanSalesMeasures.MoneyByCurrency(sales));
    }

    private static VanSalesCoverageResult BuildCoverage(
        List<VanSaleFact> sales,
        List<VanSaleLineFact> lines,
        Dictionary<VanSalesDayKey, VanRouteDayEntity> days,
        Dictionary<VanSalesDayKey, HashSet<string>> visits)
    {
        var repDaysWithoutRouteDay = sales
            .Select(sale => sale.Key)
            .Distinct()
            .Count(key => !days.ContainsKey(key));

        var repsWithVisits = visits.Keys.Select(key => key.UserId).ToHashSet();

        // A reference in both tables is counted twice by the union. The offline ingest only checks
        // incoming references against DesktopSales, never against StockReservations, so this can
        // happen — and it is an ingest defect worth surfacing, not something to silently dedupe by
        // picking a winner.
        var offlineRefs = sales
            .Where(sale => sale.Source == VanSaleSource.OfflineBatch)
            .Select(sale => sale.ExternalReferenceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var onlineRefs = sales
            .Where(sale => sale.Source == VanSaleSource.OnlineInvoice)
            .Select(sale => sale.ExternalReferenceId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        offlineRefs.IntersectWith(onlineRefs);

        return new VanSalesCoverageResult(
            SaleCount: sales.Count,
            LineCount: lines.Count,
            SalesWithoutRouteCustomer: sales.Count(sale => sale.RouteCustomerCode is null),
            SalesWithoutRouteDay: sales.Count(sale => !days.ContainsKey(sale.Key)),
            RepDaysWithoutRouteDay: repDaysWithoutRouteDay,
            // The shared rule silently reads a blank currency as USD. That is a convention, not a
            // fact, so the rows it applied to are counted rather than assumed away.
            SalesWithAssumedCurrency: sales.Count(sale => string.IsNullOrWhiteSpace(sale.Currency)),
            SalesWithoutPaymentMethod: sales.Count(sale => string.IsNullOrWhiteSpace(sale.PaymentMethod)),
            LinesWithoutUoM: lines.Count(line => string.IsNullOrWhiteSpace(line.UoMCode)),
            LinesWithZeroQuantity: lines.Count(line => line.Quantity == 0),
            RepsWithoutVisitData: sales.Select(sale => sale.UserId)
                .Distinct()
                .Count(userId => !repsWithVisits.Contains(userId)),
            ReferencesInBothSources: offlineRefs.Count);
    }

}
