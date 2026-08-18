using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Features.RouteCustomers.Queries;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.VanSalesReports.Queries.GetVanSalesCoverageReport;

/// <summary>
/// Builds the coverage and outlet development report: who the vans are reaching, and who they are
/// losing.
/// </summary>
/// <remarks>
/// Reads through <see cref="VanSalesFactReader"/> and counts through
/// <see cref="VanSalesMeasures"/>, so a strike rate here and a strike rate on the performance report
/// are the same number by construction rather than by coincidence.
///
/// Three reads beyond the window, each earning its cost:
///
/// The <b>prior window</b> establishes what the base looked like when the period opened, and holds
/// the last purchase of shops that have since gone quiet. Documents only — nothing in churn or the
/// lapsed register needs a prior line.
///
/// The <b>first-purchase scan</b> is unbounded, because a genuinely new outlet and one returning
/// after a long silence are separated by nothing else.
///
/// The <b>roster</b> is read live, which is the one thing this report cannot do honestly and says so
/// on its face: the plan behind <c>PlannedCustomerCount</c> was never snapshotted, so today's shop
/// list is the only list there is.
/// </remarks>
public sealed class GetVanSalesCoverageReportHandler(
    ApplicationDbContext db
) : IRequestHandler<GetVanSalesCoverageReportQuery, ErrorOr<VanSalesCoverageReportResult>>
{
    /// <summary>Beyond this a fix is too vague to place a rep on a street, let alone at a door.</summary>
    private const int PoorAccuracyMetres = 250;

    /// <summary>How many shops a concentration row names before it stops listing them.</summary>
    private const int TopOutletsPerRoute = 10;

    public async Task<ErrorOr<VanSalesCoverageReportResult>> Handle(
        GetVanSalesCoverageReportQuery query,
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

        if (query.LapseDays < 1)
        {
            return Error.Validation(
                "VanSalesReports.InvalidLapseDays",
                "A shop has to be given at least a day to buy again before it counts as lapsed.");
        }

        // Far enough back to know the opening state (which needs a full lapse window) with room left
        // for the win-back register to reach shops that went quiet before the period opened.
        var priorDays = Math.Min(VanSalesFacts.MaximumDays, query.LapseDays + 90);
        var priorFrom = from.AddDays(-priorDays);
        var priorTo = from.AddDays(-1);

        var sales = await VanSalesFactReader.LoadSalesAsync(
            db, new VanSalesFactFilter(from, to, query.UserId), cancellationToken);

        var lines = await VanSalesFactReader.LoadSaleLinesAsync(
            db, new VanSalesFactFilter(from, to, query.UserId), cancellationToken);

        var priorSales = await VanSalesFactReader.LoadSalesAsync(
            db, new VanSalesFactFilter(priorFrom, priorTo, query.UserId), cancellationToken);

        var days = await LoadRouteDaysAsync(query, from, to, cancellationToken);
        var calls = await LoadCallsAsync(query, from, to, cancellationToken);
        var priorCalls = await LoadLastVisitsAsync(query, priorFrom, priorTo, cancellationToken);
        var firstPurchases = await VanSalesFactReader.LoadFirstPurchaseDatesAsync(db, cancellationToken);

        // The route filter refuses to be satisfied by a rep-day with no departure record, for the
        // reason both earlier reports give: nothing on such a sale says which route it belonged to.
        if (!string.IsNullOrWhiteSpace(query.RouteCode))
        {
            var routeCode = query.RouteCode.Trim();

            bool OnRoute(VanSalesDayKey key) =>
                days.TryGetValue(key, out var day)
                && string.Equals(day.RouteCode, routeCode, StringComparison.OrdinalIgnoreCase);

            sales = sales.Where(sale => OnRoute(sale.Key)).ToList();
            lines = lines.Where(line => OnRoute(line.Key)).ToList();
        }

        // Reps from the prior window too, and deliberately. A rep who sold nothing this period is the
        // one whose whole roster is uncovered — scoping the roster to reps who traded would drop
        // exactly the case the register exists to show.
        var repIds = sales.Select(sale => sale.UserId)
            .Concat(priorSales.Select(sale => sale.UserId))
            .Concat(calls.Select(call => call.UserId))
            .Distinct()
            .ToList();

        var reps = await LoadRepsAsync(repIds, cancellationToken);
        var roster = await LoadRosterAsync(reps.Values.Select(rep => rep.AccountCode), cancellationToken);

        var visitsByDay = calls
            .GroupBy(call => new VanSalesDayKey(call.UserId, call.TradingDate))
            .ToDictionary(
                group => group.Key,
                group => group.Select(call => call.CustomerCode).ToHashSet(StringComparer.OrdinalIgnoreCase));

        var buckets = BuildBuckets(from, to, query.Granularity);

        var result = new VanSalesCoverageReportResult(
            FromDate: from,
            ToDate: to,
            PriorWindowFrom: priorFrom,
            LapseDays: query.LapseDays,
            Granularity: query.Granularity,
            Summary: BuildSummary(sales, days, visitsByDay, calls, roster, reps, priorSales, firstPurchases, query, to),
            Trend: BuildTrend(buckets, sales, days, calls, from, to),
            Reps: BuildReps(sales, days, visitsByDay, calls, roster, reps),
            UncoveredOutlets: BuildUncovered(roster, reps, calls, priorCalls, sales, priorSales, to),
            LocationIntegrity: BuildLocationIntegrity(calls, reps),
            Churn: BuildChurn(buckets, sales, priorSales, firstPurchases, query.LapseDays, priorFrom),
            LapsedOutlets: BuildLapsed(sales, priorSales, roster, reps, days, priorCalls, query.LapseDays, to),
            Concentration: BuildConcentration(sales, days),
            Outlets: BuildOutlets(sales, lines, calls, firstPurchases),
            Quality: BuildQuality(sales, days, roster, reps, visitsByDay, priorSales, from));

        return result;
    }

    // ── Reads ───────────────────────────────────────────────────────────────────

    private async Task<Dictionary<VanSalesDayKey, VanRouteDayEntity>> LoadRouteDaysAsync(
        GetVanSalesCoverageReportQuery query,
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
    /// Every van call in the window, with its location record intact.
    /// </summary>
    /// <remarks>
    /// The whole row rather than a per-day set, because the integrity panel needs the fix on each
    /// call. The per-day sets the rate measures use are derived from this, so there is one read and
    /// one truth rather than two queries that can drift apart.
    ///
    /// The channel is pinned and is never a parameter — a query that takes a channel belongs in the
    /// merchandiser feature folder.
    /// </remarks>
    private async Task<List<CallRow>> LoadCallsAsync(
        GetVanSalesCoverageReportQuery query,
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
            .Select(entry => new
            {
                entry.UserId,
                entry.CheckInTime,
                entry.CheckInRecordedAt,
                entry.CustomerCode,
                entry.CheckInLatitude,
                entry.CheckInLocationSource,
                entry.CheckInLocationAccuracyMetres
            })
            .ToListAsync(cancellationToken);

        return entries
            .Select(entry => new CallRow(
                entry.UserId,
                VanSalesFacts.TradingDayOf(entry.CheckInTime),
                entry.CustomerCode,
                entry.CheckInTime,
                entry.CheckInLocationSource,
                entry.CheckInLocationAccuracyMetres,
                entry.CheckInLatitude.HasValue,
                // The entity's own two-minute rule. Routine on a van, so it is reported as context
                // rather than as a fault.
                entry.CheckInRecordedAt.HasValue
                && (entry.CheckInRecordedAt.Value - entry.CheckInTime).TotalMinutes > 2))
            .ToList();
    }

    /// <summary>
    /// The last time each shop was called on before the window, which is what separates a shop that
    /// has never been visited from one that simply was not reached this period.
    /// </summary>
    private async Task<Dictionary<string, DateTime>> LoadLastVisitsAsync(
        GetVanSalesCoverageReportQuery query,
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
            .GroupBy(entry => entry.CustomerCode)
            .Select(group => new { Code = group.Key, Last = group.Max(entry => entry.CheckInTime) })
            .ToListAsync(cancellationToken);

        return entries.ToDictionary(
            entry => entry.Code,
            entry => VanSalesFacts.TradingDayOf(entry.Last),
            StringComparer.OrdinalIgnoreCase);
    }

    private async Task<Dictionary<Guid, RepRow>> LoadRepsAsync(
        List<Guid> repIds,
        CancellationToken cancellationToken)
    {
        if (repIds.Count == 0)
        {
            return [];
        }

        var users = await db.Users
            .AsNoTracking()
            .Where(user => repIds.Contains(user.Id))
            .Select(user => new
            {
                user.Id,
                user.Username,
                user.FirstName,
                user.LastName,
                user.AssignedBusinessPartnerCode
            })
            .ToListAsync(cancellationToken);

        return users.ToDictionary(
            user => user.Id,
            user => new RepRow(
                user.Username,
                string.IsNullOrWhiteSpace($"{user.FirstName} {user.LastName}".Trim())
                    ? null
                    : $"{user.FirstName} {user.LastName}".Trim(),
                string.IsNullOrWhiteSpace(user.AssignedBusinessPartnerCode)
                    ? null
                    : user.AssignedBusinessPartnerCode.Trim()));
    }

    /// <summary>
    /// The shops on the books, per van account.
    /// </summary>
    /// <remarks>
    /// Active only, because that is the list the handset is given and the register must not disagree
    /// with what the rep was actually shown. Deactivated shops keep their row — deleting a route
    /// customer sets <c>IsActive</c> false rather than removing it — so they stay out of coverage
    /// while their history stays intact.
    /// </remarks>
    private async Task<Dictionary<string, List<RosterRow>>> LoadRosterAsync(
        IEnumerable<string?> accountCodes,
        CancellationToken cancellationToken)
    {
        var accounts = accountCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (accounts.Count == 0)
        {
            return [];
        }

        var customers = await db.RouteCustomers
            .AsNoTracking()
            .Where(customer => accounts.Contains(customer.AssignedBusinessPartnerCode) && customer.IsActive)
            .Select(customer => new
            {
                customer.AssignedBusinessPartnerCode,
                customer.Code,
                customer.Name,
                customer.Phone,
                customer.CreatedAt
            })
            .ToListAsync(cancellationToken);

        return customers
            .GroupBy(customer => customer.AssignedBusinessPartnerCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(customer => new RosterRow(
                        customer.Code,
                        customer.Name,
                        customer.Phone,
                        VanSalesFacts.TradingDayOf(customer.CreatedAt)))
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);
    }

    // ── Buckets ─────────────────────────────────────────────────────────────────

    private static List<Bucket> BuildBuckets(
        DateTime from,
        DateTime to,
        VanSalesCoverageGranularity granularity)
    {
        var buckets = new List<Bucket>();

        if (granularity == VanSalesCoverageGranularity.Week)
        {
            // Weeks run Monday to Sunday, which is how a route is planned and talked about.
            var start = from.AddDays(-(((int)from.DayOfWeek + 6) % 7));

            while (start <= to)
            {
                var end = start.AddDays(6);
                buckets.Add(new Bucket(
                    $"w/c {start:dd MMM}", start, end, start < from || end > to));
                start = start.AddDays(7);
            }

            return buckets;
        }

        var month = new DateTime(from.Year, from.Month, 1);

        while (month <= to)
        {
            var end = month.AddMonths(1).AddDays(-1);
            buckets.Add(new Bucket(
                month.ToString("MMM yyyy"), month, end, month < from || end > to));
            month = month.AddMonths(1);
        }

        return buckets;
    }

    // ── B2 / B5: the rate trend ─────────────────────────────────────────────────

    private static List<VanSalesCoverageTrendPointResult> BuildTrend(
        List<Bucket> buckets,
        List<VanSaleFact> sales,
        Dictionary<VanSalesDayKey, VanRouteDayEntity> days,
        List<CallRow> calls,
        DateTime from,
        DateTime to) =>
        buckets
            .Select(bucket =>
            {
                var start = bucket.Start < from ? from : bucket.Start;
                var end = bucket.End > to ? to : bucket.End;

                var bucketSales = sales
                    .Where(sale => sale.TradingDate >= start && sale.TradingDate <= end)
                    .ToList();

                var bucketCalls = calls
                    .Where(call => call.TradingDate >= start && call.TradingDate <= end)
                    .ToList();

                var bucketDays = days.Values
                    .Where(day => day.TradingDate >= start && day.TradingDate <= end)
                    .ToList();

                // A plan of zero is the failure branch of the handset's own count, not a plan. It is
                // excluded from both sides of the rate — admitting it would turn an outage into
                // non-compliance — and reported separately.
                var planned = bucketDays.Where(day => day.PlannedCustomerCount > 0).ToList();

                var callsAgainstPlan = planned.Count == 0
                    ? null
                    : (int?)planned.Sum(day => bucketCalls
                        .Where(call => call.UserId == day.UserId && call.TradingDate == day.TradingDate)
                        .Select(call => call.CustomerCode)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count());

                var activeDayKeys = bucketSales.Select(sale => sale.Key)
                    .Concat(bucketCalls.Select(call => new VanSalesDayKey(call.UserId, call.TradingDate)))
                    .Distinct()
                    .ToList();

                return new VanSalesCoverageTrendPointResult(
                    Label: bucket.Label,
                    BucketStart: start,
                    BucketEnd: end,
                    IsPartial: bucket.IsPartial,
                    RepsTrading: bucketSales.Select(sale => sale.UserId).Distinct().Count(),
                    PlannedCalls: planned.Count == 0 ? null : planned.Sum(day => day.PlannedCustomerCount),
                    Calls: CallsIn(bucketCalls),
                    ProductiveCalls: VanSalesMeasures.CountProductiveCalls(bucketSales),
                    OutletsBought: VanSalesMeasures.CountOutletsThatBought(bucketSales),
                    DaysWithoutPlan: bucketDays.Count(day => day.PlannedCustomerCount == 0),
                    RepDaysWithoutRouteDay: activeDayKeys.Count(key => !days.ContainsKey(key)));
            })
            .ToList();

    /// <summary>Distinct shops called on per rep-day, summed. Null when nothing was recorded.</summary>
    private static int? CallsIn(List<CallRow> calls) =>
        calls.Count == 0
            ? null
            : calls
                .GroupBy(call => new VanSalesDayKey(call.UserId, call.TradingDate))
                .Sum(day => day.Select(call => call.CustomerCode)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count());

    // ── Reps ────────────────────────────────────────────────────────────────────

    private static List<VanSalesRepCoverageResult> BuildReps(
        List<VanSaleFact> sales,
        Dictionary<VanSalesDayKey, VanRouteDayEntity> days,
        Dictionary<VanSalesDayKey, HashSet<string>> visitsByDay,
        List<CallRow> calls,
        Dictionary<string, List<RosterRow>> roster,
        Dictionary<Guid, RepRow> reps) =>
        sales.Select(sale => sale.UserId)
            .Concat(calls.Select(call => call.UserId))
            .Distinct()
            .Select(userId =>
            {
                reps.TryGetValue(userId, out var rep);

                var repSales = sales.Where(sale => sale.UserId == userId).ToList();
                var repCalls = calls.Where(call => call.UserId == userId).ToList();

                var dayKeys = repSales.Select(sale => sale.Key)
                    .Concat(repCalls.Select(call => new VanSalesDayKey(call.UserId, call.TradingDate)))
                    .Distinct()
                    .ToList();

                var dayRecords = dayKeys
                    .Where(days.ContainsKey)
                    .Select(key => days[key])
                    .ToList();

                var account = rep?.AccountCode;

                // A van selling to real business partners records no shop on its sales at all, so
                // every outlet figure below is unknowable rather than zero.
                var attributable = repSales.Count == 0 || repSales.Any(sale => sale.RouteCustomerCode is not null);

                List<RosterRow>? repRoster = null;
                if (account is not null)
                {
                    roster.TryGetValue(account, out repRoster);
                }

                var visited = repCalls
                    .Select(call => call.CustomerCode)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var bought = repSales
                    .Where(sale => sale.RouteCustomerCode is not null)
                    .Select(sale => sale.RouteCustomerCode!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var planned = dayRecords.Where(day => day.PlannedCustomerCount > 0).ToList();

                return new VanSalesRepCoverageResult(
                    UserId: userId,
                    Username: rep?.Username ?? userId.ToString(),
                    FullName: rep?.FullName,
                    VanAccountCode: account,
                    Routes: dayRecords
                        .Select(day => day.RouteCode)
                        .Where(code => !string.IsNullOrWhiteSpace(code))
                        .Select(code => code!)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    OutletsAttributable: attributable,
                    RosterSize: repRoster?.Count,
                    TradingDayCount: dayKeys.Count,
                    Calls: VanSalesMeasures.CountCalls(dayKeys, visitsByDay),
                    OutletsVisited: VanSalesMeasures.CountOutletsVisited(dayKeys, visitsByDay),
                    ProductiveCalls: VanSalesMeasures.CountProductiveCalls(repSales),
                    OutletsBought: attributable ? bought.Count : null,
                    OutletsUncovered: repRoster is null || !attributable
                        ? null
                        : repRoster.Count(row => !visited.Contains(row.Code)),
                    PlannedCalls: planned.Count == 0 ? null : planned.Sum(day => day.PlannedCustomerCount),
                    KilometresTravelled: VanSalesMeasures.SumKilometres(dayRecords),
                    EfficiencyByCurrency: BuildEfficiency(repSales, days),
                    TotalsByCurrency: VanSalesMeasures.MoneyByCurrency(repSales));
            })
            .OrderByDescending(rep => rep.TotalsByCurrency.Sum(total => total.Gross))
            .ThenBy(rep => rep.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static List<VanSalesEfficiencyResult> BuildEfficiency(
        List<VanSaleFact> sales,
        Dictionary<VanSalesDayKey, VanRouteDayEntity> days) =>
        sales
            .GroupBy(sale => RouteCustomerSalesReporting.NormalizeCurrency(sale.Currency),
                StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                // The distance of the days this currency actually traded on. A day that took both
                // currencies contributes to both, which is why these never sum to the rep's total.
                var currencyDays = group.Select(sale => sale.Key).Distinct()
                    .Where(days.ContainsKey)
                    .Select(key => days[key])
                    .ToList();

                return new VanSalesEfficiencyResult(
                    Currency: group.Key,
                    Gross: group.Sum(sale => sale.TotalAmount),
                    DropCount: group.Select(VanSalesMeasures.DropKey).Distinct().Count(),
                    Kilometres: VanSalesMeasures.SumKilometres(currencyDays),
                    DaysWithMileage: currencyDays.Count(day =>
                        day.StartingMileage.HasValue && day.ClosingMileage.HasValue),
                    DaysWithoutMileage: currencyDays.Count(day =>
                        !day.StartingMileage.HasValue || !day.ClosingMileage.HasValue));
            })
            .OrderByDescending(efficiency => efficiency.Gross)
            .ThenBy(efficiency => efficiency.Currency, StringComparer.OrdinalIgnoreCase)
            .ToList();

    // ── B7: the uncovered register ──────────────────────────────────────────────

    private static List<VanSalesUncoveredOutletResult> BuildUncovered(
        Dictionary<string, List<RosterRow>> roster,
        Dictionary<Guid, RepRow> reps,
        List<CallRow> calls,
        Dictionary<string, DateTime> priorCalls,
        List<VanSaleFact> sales,
        List<VanSaleFact> priorSales,
        DateTime to)
    {
        var visited = calls.Select(call => call.CustomerCode).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var bought = sales
            .Select(sale => sale.Outlet)
            .Where(outlet => outlet.HasValue)
            .Select(outlet => outlet!.Value)
            .ToHashSet();

        // The last purchase anywhere in the read, so a shop can be told from one that has never bought.
        var lastPurchase = sales.Concat(priorSales)
            .Where(sale => sale.Outlet.HasValue)
            .GroupBy(sale => sale.Outlet!.Value)
            .ToDictionary(group => group.Key, group => group.Max(sale => sale.TradingDate));

        var owners = reps.Values
            .Where(rep => rep.AccountCode is not null)
            .GroupBy(rep => rep.AccountCode!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(rep => rep.DisplayName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);

        var rows = new List<VanSalesUncoveredOutletResult>();

        foreach (var (account, outlets) in roster)
        {
            foreach (var outlet in outlets)
            {
                var key = new VanSalesOutletKey(account, outlet.Code);
                var wasVisited = visited.Contains(outlet.Code);

                if (wasVisited && bought.Contains(key))
                {
                    continue;
                }

                var gap = wasVisited
                    ? VanSalesCoverageGap.VisitedNotBought
                    : priorCalls.ContainsKey(outlet.Code)
                        ? VanSalesCoverageGap.NotVisitedInWindow
                        : VanSalesCoverageGap.NeverVisited;

                lastPurchase.TryGetValue(key, out var last);
                var lastOn = last == default ? (DateTime?)null : last;

                owners.TryGetValue(account, out var owningReps);
                priorCalls.TryGetValue(outlet.Code, out var lastVisit);

                rows.Add(new VanSalesUncoveredOutletResult(
                    VanAccountCode: account,
                    OutletCode: outlet.Code,
                    OutletName: outlet.Name,
                    Phone: outlet.Phone,
                    Gap: gap,
                    LastVisitedOn: wasVisited
                        ? calls.Where(call =>
                                string.Equals(call.CustomerCode, outlet.Code, StringComparison.OrdinalIgnoreCase))
                            .Max(call => call.TradingDate)
                        : lastVisit == default ? null : lastVisit,
                    LastPurchaseOn: lastOn,
                    DaysSinceLastPurchase: lastOn is { } day ? Math.Max(0, (int)(to - day).TotalDays) : null,
                    CapturedOn: outlet.CapturedOn,
                    OwningReps: owningReps ?? []));
            }
        }

        return rows
            // Never visited first: those are the shops nobody has been to at all.
            .OrderBy(row => row.Gap)
            .ThenByDescending(row => row.DaysSinceLastPurchase ?? int.MaxValue)
            .ThenBy(row => row.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ── B8: location integrity ──────────────────────────────────────────────────

    private static VanSalesLocationIntegrityResult BuildLocationIntegrity(
        List<CallRow> calls,
        Dictionary<Guid, RepRow> reps)
    {
        static bool IsGps(CallRow call) =>
            string.Equals(call.LocationSource, TimesheetLocationSources.Gps, StringComparison.OrdinalIgnoreCase)
            && call.HasCoordinates;

        static bool IsLastKnown(CallRow call) =>
            string.Equals(call.LocationSource, TimesheetLocationSources.LastKnown, StringComparison.OrdinalIgnoreCase);

        // No coordinates at all, or a source that says so. Both mean the position is absent rather
        // than merely imprecise.
        static bool HasNoFix(CallRow call) =>
            !call.HasCoordinates
            || string.Equals(call.LocationSource, TimesheetLocationSources.None, StringComparison.OrdinalIgnoreCase);

        // An unstated accuracy is not an accurate fix. It is excluded rather than passed.
        static bool IsPoor(CallRow call) =>
            call.AccuracyMetres is { } metres && metres > PoorAccuracyMetres;

        return new VanSalesLocationIntegrityResult(
            CallCount: calls.Count,
            CallsWithGpsFix: calls.Count(IsGps),
            CallsWithLastKnownFix: calls.Count(IsLastKnown),
            CallsWithNoFix: calls.Count(HasNoFix),
            CallsWithoutAccuracy: calls.Count(call => call.AccuracyMetres is null),
            CallsWithPoorAccuracy: calls.Count(IsPoor),
            CallsCapturedOffline: calls.Count(call => call.CapturedOffline),
            PoorAccuracyMetres: PoorAccuracyMetres,
            Reps: calls
                .GroupBy(call => call.UserId)
                .Select(group =>
                {
                    reps.TryGetValue(group.Key, out var rep);

                    return new VanSalesRepLocationResult(
                        UserId: group.Key,
                        Username: rep?.Username ?? group.Key.ToString(),
                        FullName: rep?.FullName,
                        CallCount: group.Count(),
                        CallsWithGpsFix: group.Count(IsGps),
                        CallsWithLastKnownFix: group.Count(IsLastKnown),
                        CallsWithNoFix: group.Count(HasNoFix),
                        CallsWithPoorAccuracy: group.Count(IsPoor),
                        CallsCapturedOffline: group.Count(call => call.CapturedOffline));
                })
                .OrderBy(rep => rep.GpsFixRate ?? 0)
                .ThenBy(rep => rep.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList());
    }

    // ── E1: churn ───────────────────────────────────────────────────────────────

    private static List<VanSalesChurnPointResult> BuildChurn(
        List<Bucket> buckets,
        List<VanSaleFact> sales,
        List<VanSaleFact> priorSales,
        Dictionary<VanSalesOutletKey, DateTime> firstPurchases,
        int lapseDays,
        DateTime priorFrom)
    {
        // Every purchase day per shop across both windows. The state of an outlet on any date is a
        // question about this set and nothing else — never about whether its row still exists.
        var purchaseDays = sales.Concat(priorSales)
            .Where(sale => sale.Outlet.HasValue)
            .GroupBy(sale => sale.Outlet!.Value)
            .ToDictionary(
                group => group.Key,
                group => group.Select(sale => sale.TradingDate).Distinct().OrderBy(date => date).ToList());

        var earliest = firstPurchases.Count == 0 ? (DateTime?)null : firstPurchases.Values.Min();

        DateTime? LastPurchaseOnOrBefore(VanSalesOutletKey outlet, DateTime date) =>
            purchaseDays.TryGetValue(outlet, out var dates)
                ? dates.Where(day => day <= date).Cast<DateTime?>().LastOrDefault()
                : null;

        bool IsActiveAt(VanSalesOutletKey outlet, DateTime date) =>
            LastPurchaseOnOrBefore(outlet, date) is { } last && (date - last).TotalDays <= lapseDays;

        var universe = purchaseDays.Keys.ToList();
        var points = new List<VanSalesChurnPointResult>(buckets.Count);

        foreach (var bucket in buckets)
        {
            var previousEnd = bucket.Start.AddDays(-1);

            var bucketSales = sales
                .Where(sale => sale.TradingDate >= bucket.Start && sale.TradingDate <= bucket.End)
                .ToList();

            var boughtInBucket = bucketSales
                .Where(sale => sale.Outlet.HasValue)
                .Select(sale => sale.Outlet!.Value)
                .ToHashSet();

            var opening = universe.Count(outlet => IsActiveAt(outlet, previousEnd));
            var closing = universe.Count(outlet => IsActiveAt(outlet, bucket.End));

            var newOutlets = boughtInBucket.Count(outlet =>
                firstPurchases.TryGetValue(outlet, out var first)
                && first >= bucket.Start && first <= bucket.End);

            // Bought in the bucket, having bought at some point before it, and not active when it
            // opened. "Bought before" is decided by the unbounded first-purchase scan and not by the
            // loaded windows: a shop that last bought two years ago has no row in either window, and
            // testing the windows alone would file it as neither new nor returning — so it would
            // vanish from the movement while still appearing in the closing base.
            var reactivated = boughtInBucket.Count(outlet =>
                firstPurchases.TryGetValue(outlet, out var first)
                && first < bucket.Start
                && !IsActiveAt(outlet, previousEnd));

            // Crossed the line inside this bucket. Counted once, at the boundary it crossed.
            var lapsed = universe.Count(outlet =>
                IsActiveAt(outlet, previousEnd) && !IsActiveAt(outlet, bucket.End));

            var newSales = bucketSales
                .Where(sale => sale.Outlet is { } outlet
                               && firstPurchases.TryGetValue(outlet, out var first)
                               && first >= bucket.Start && first <= bucket.End)
                .ToList();

            points.Add(new VanSalesChurnPointResult(
                Label: bucket.Label,
                BucketStart: bucket.Start,
                BucketEnd: bucket.End,
                IsPartial: bucket.IsPartial,
                // The van tables only reach back so far. A bucket at the edge shows every outlet
                // trading before it as new, which is an artefact of the data's start, not growth.
                IsCensored: earliest is { } start && bucket.Start <= start.AddDays(lapseDays),
                OpeningActiveOutlets: opening,
                BuyingOutlets: boughtInBucket.Count,
                NewOutlets: newOutlets,
                ReactivatedOutlets: reactivated,
                LapsedOutlets: lapsed,
                ClosingActiveOutlets: closing,
                TotalsByCurrency: VanSalesMeasures.MoneyByCurrency(bucketSales),
                NewOutletTotalsByCurrency: VanSalesMeasures.MoneyByCurrency(newSales)));
        }

        return points;
    }

    // ── E3: the lapsed register ─────────────────────────────────────────────────

    private static List<VanSalesLapsedOutletResult> BuildLapsed(
        List<VanSaleFact> sales,
        List<VanSaleFact> priorSales,
        Dictionary<string, List<RosterRow>> roster,
        Dictionary<Guid, RepRow> reps,
        Dictionary<VanSalesDayKey, VanRouteDayEntity> days,
        Dictionary<string, DateTime> priorCalls,
        int lapseDays,
        DateTime to)
    {
        var all = sales.Concat(priorSales)
            .Where(sale => sale.Outlet.HasValue)
            .ToList();

        var rosterByKey = roster
            .SelectMany(pair => pair.Value.Select(row => (Key: new VanSalesOutletKey(pair.Key, row.Code), Row: row)))
            .ToDictionary(entry => entry.Key, entry => entry.Row);

        var owners = reps.Values
            .Where(rep => rep.AccountCode is not null)
            .GroupBy(rep => rep.AccountCode!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(rep => rep.DisplayName).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                StringComparer.OrdinalIgnoreCase);

        return all
            .GroupBy(sale => sale.Outlet!.Value)
            .Select(group =>
            {
                var lastDay = group.Max(sale => sale.TradingDate);
                var daysSince = Math.Max(0, (int)(to - lastDay).TotalDays);

                if (daysSince <= lapseDays)
                {
                    return null;
                }

                // The whole of the final drop, not the final document: two invoices at one counter
                // are one visit's takings.
                var lastDrop = group.Where(sale => sale.TradingDate == lastDay).ToList();
                var lastRep = lastDrop[0];

                rosterByKey.TryGetValue(group.Key, out var rosterRow);
                owners.TryGetValue(group.Key.VanAccountCode, out var owningReps);
                reps.TryGetValue(lastRep.UserId, out var soldBy);
                days.TryGetValue(lastRep.Key, out var routeDay);
                priorCalls.TryGetValue(group.Key.OutletCode, out var lastVisit);

                return new VanSalesLapsedOutletResult(
                    VanAccountCode: group.Key.VanAccountCode,
                    OutletCode: group.Key.OutletCode,
                    OutletName: rosterRow?.Name ?? group.First().RouteCustomerName,
                    Phone: rosterRow?.Phone,
                    LastPurchaseOn: lastDay,
                    DaysSinceLastPurchase: daysSince,
                    LastVisitedOn: lastVisit == default ? null : lastVisit,
                    LastSoldByRep: soldBy?.DisplayName,
                    LastRouteCode: routeDay?.RouteCode,
                    // A shop off the roster has been dropped deliberately; one still on it and not
                    // buying is the one worth a phone call.
                    StillOnRoster: rosterRow is not null,
                    PriorPurchaseDayCount: group.Select(sale => sale.TradingDate).Distinct().Count(),
                    LastPurchaseByCurrency: VanSalesMeasures.MoneyByCurrency(lastDrop),
                    PriorTotalsByCurrency: VanSalesMeasures.MoneyByCurrency(group),
                    OwningReps: owningReps ?? []);
            })
            .Where(row => row is not null)
            .Select(row => row!)
            .OrderByDescending(row => row.PriorTotalsByCurrency.Sum(total => total.Gross))
            .ThenBy(row => row.DaysSinceLastPurchase)
            .ToList();
    }

    // ── E4: concentration ───────────────────────────────────────────────────────

    private static List<VanSalesConcentrationResult> BuildConcentration(
        List<VanSaleFact> sales,
        Dictionary<VanSalesDayKey, VanRouteDayEntity> days) =>
        sales
            .GroupBy(sale => VanSalesMeasures.RouteKeyOf(sale.Key, days))
            .SelectMany(routeGroup => routeGroup
                .GroupBy(sale => RouteCustomerSalesReporting.NormalizeCurrency(sale.Currency),
                    StringComparer.OrdinalIgnoreCase)
                .Select(currencyGroup =>
                {
                    var ranked = currencyGroup
                        .Where(sale => sale.Outlet.HasValue)
                        .GroupBy(sale => sale.Outlet!.Value)
                        .Select(outlet => new
                        {
                            outlet.Key.OutletCode,
                            Name = outlet.First().RouteCustomerName,
                            Gross = outlet.Sum(sale => sale.TotalAmount)
                        })
                        .OrderByDescending(outlet => outlet.Gross)
                        .ThenBy(outlet => outlet.OutletCode, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    var attributed = ranked.Sum(outlet => outlet.Gross);

                    decimal TopN(int n) => ranked.Take(n).Sum(outlet => outlet.Gross);

                    int? HalfPoint()
                    {
                        if (attributed <= 0 || ranked.Count == 0)
                        {
                            return null;
                        }

                        decimal running = 0;
                        for (var index = 0; index < ranked.Count; index++)
                        {
                            running += ranked[index].Gross;
                            if (running / attributed >= 0.5m)
                            {
                                return index + 1;
                            }
                        }

                        return ranked.Count;
                    }

                    return new VanSalesConcentrationResult(
                        HasRouteDay: routeGroup.Key.HasRouteDay,
                        RouteCode: routeGroup.Key.RouteCode,
                        RouteName: routeGroup.Key.RouteName,
                        Currency: currencyGroup.Key,
                        OutletCount: ranked.Count,
                        AttributedGross: attributed,
                        UnattributedGross: currencyGroup
                            .Where(sale => !sale.Outlet.HasValue)
                            .Sum(sale => sale.TotalAmount),
                        Top1Gross: TopN(1),
                        Top5Gross: TopN(5),
                        Top10Gross: TopN(10),
                        OutletsForHalfOfGross: HalfPoint(),
                        TopOutlets: ranked
                            .Take(TopOutletsPerRoute)
                            .Select((outlet, index) => new VanSalesOutletShareResult(
                                Rank: index + 1,
                                OutletCode: outlet.OutletCode,
                                OutletName: outlet.Name,
                                Gross: outlet.Gross,
                                SharePercent: attributed == 0
                                    ? 0
                                    : (double)decimal.Round(outlet.Gross / attributed * 100m, 2)))
                            .ToList());
                }))
            .OrderBy(row => row.HasRouteDay ? 0 : 1)
            .ThenByDescending(row => row.AttributedGross)
            .ToList();

    // ── E5: frequency and basket depth ──────────────────────────────────────────

    private static List<VanSalesOutletActivityResult> BuildOutlets(
        List<VanSaleFact> sales,
        List<VanSaleLineFact> lines,
        List<CallRow> calls,
        Dictionary<VanSalesOutletKey, DateTime> firstPurchases)
    {
        var linesByOutlet = lines
            .Where(line => line.Outlet.HasValue)
            .GroupBy(line => line.Outlet!.Value)
            .ToDictionary(group => group.Key, group => group.ToList());

        var visitsByCode = calls
            .GroupBy(call => call.CustomerCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        return sales
            .Where(sale => sale.Outlet.HasValue)
            .GroupBy(sale => sale.Outlet!.Value)
            .Select(group =>
            {
                var purchaseDays = group.Select(sale => sale.TradingDate).Distinct().OrderBy(day => day).ToList();

                linesByOutlet.TryGetValue(group.Key, out var outletLines);
                outletLines ??= [];

                // Depth is a count of distinct items in a drop, averaged over drops — never a
                // quantity, which van lines cannot express.
                var itemsPerDrop = purchaseDays
                    .Select(day => outletLines
                        .Where(line => line.TradingDate == day)
                        .Select(line => line.ItemCode)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count())
                    .ToList();

                double? gap = purchaseDays.Count < 2
                    ? null
                    : (purchaseDays[^1] - purchaseDays[0]).TotalDays / (purchaseDays.Count - 1);

                firstPurchases.TryGetValue(group.Key, out var firstEver);
                visitsByCode.TryGetValue(group.Key.OutletCode, out var visitCount);

                return new VanSalesOutletActivityResult(
                    VanAccountCode: group.Key.VanAccountCode,
                    OutletCode: group.Key.OutletCode,
                    OutletName: group.First().RouteCustomerName,
                    VisitCount: calls.Count == 0 ? null : visitCount,
                    PurchaseDayCount: purchaseDays.Count,
                    DocumentCount: group.Count(),
                    DistinctItemCount: outletLines
                        .Select(line => line.ItemCode)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count(),
                    AverageItemsPerPurchase: itemsPerDrop.Count == 0
                        ? 0m
                        : decimal.Round((decimal)itemsPerDrop.Average(), 1),
                    FirstPurchaseInWindow: purchaseDays[0],
                    LastPurchaseInWindow: purchaseDays[^1],
                    FirstEverPurchaseOn: firstEver == default ? null : firstEver,
                    AverageDaysBetweenPurchases: gap,
                    TotalsByCurrency: VanSalesMeasures.MoneyByCurrency(group));
            })
            .OrderByDescending(outlet => outlet.TotalsByCurrency.Sum(total => total.Gross))
            .ThenBy(outlet => outlet.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ── Summary and quality ─────────────────────────────────────────────────────

    private static VanSalesCoverageSummaryResult BuildSummary(
        List<VanSaleFact> sales,
        Dictionary<VanSalesDayKey, VanRouteDayEntity> days,
        Dictionary<VanSalesDayKey, HashSet<string>> visitsByDay,
        List<CallRow> calls,
        Dictionary<string, List<RosterRow>> roster,
        Dictionary<Guid, RepRow> reps,
        List<VanSaleFact> priorSales,
        Dictionary<VanSalesOutletKey, DateTime> firstPurchases,
        GetVanSalesCoverageReportQuery query,
        DateTime to)
    {
        var dayKeys = sales.Select(sale => sale.Key)
            .Concat(calls.Select(call => new VanSalesDayKey(call.UserId, call.TradingDate)))
            .Distinct()
            .ToList();

        var dayRecords = dayKeys.Where(days.ContainsKey).Select(key => days[key]).ToList();
        var planned = dayRecords.Where(day => day.PlannedCustomerCount > 0).ToList();

        var visited = calls.Select(call => call.CustomerCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rosterSize = roster.Count == 0 ? (int?)null : roster.Sum(pair => pair.Value.Count);

        var boughtKeys = sales
            .Where(sale => sale.Outlet.HasValue)
            .Select(sale => sale.Outlet!.Value)
            .ToHashSet();

        var newOutlets = boughtKeys.Count(outlet =>
            firstPurchases.TryGetValue(outlet, out var first) && first >= sales.Min(s => s.TradingDate));

        var priorKeys = priorSales
            .Where(sale => sale.Outlet.HasValue)
            .Select(sale => sale.Outlet!.Value)
            .ToHashSet();

        return new VanSalesCoverageSummaryResult(
            RepCount: dayKeys.Select(key => key.UserId).Distinct().Count(),
            RosterSize: rosterSize,
            OutletsVisited: visited.Count,
            OutletsBought: boughtKeys.Count,
            OutletsUncovered: roster
                .SelectMany(pair => pair.Value)
                .Count(row => !visited.Contains(row.Code)),
            NewOutlets: sales.Count == 0 ? 0 : newOutlets,
            ReactivatedOutlets: boughtKeys.Count(outlet =>
                priorKeys.Contains(outlet)
                && !(firstPurchases.TryGetValue(outlet, out var first)
                     && sales.Count > 0 && first >= sales.Min(s => s.TradingDate))),
            LapsedOutlets: priorKeys.Count(outlet => !boughtKeys.Contains(outlet)),
            Calls: VanSalesMeasures.CountCalls(dayKeys, visitsByDay),
            ProductiveCalls: VanSalesMeasures.CountProductiveCalls(sales),
            PlannedCalls: planned.Count == 0 ? null : planned.Sum(day => day.PlannedCustomerCount),
            KilometresTravelled: VanSalesMeasures.SumKilometres(dayRecords),
            TotalsByCurrency: VanSalesMeasures.MoneyByCurrency(sales));
    }

    private static VanSalesCoverageQualityResult BuildQuality(
        List<VanSaleFact> sales,
        Dictionary<VanSalesDayKey, VanRouteDayEntity> days,
        Dictionary<string, List<RosterRow>> roster,
        Dictionary<Guid, RepRow> reps,
        Dictionary<VanSalesDayKey, HashSet<string>> visitsByDay,
        List<VanSaleFact> priorSales,
        DateTime from)
    {
        var repsWithVisits = visitsByDay.Keys.Select(key => key.UserId).ToHashSet();
        var salesByRep = sales.GroupBy(sale => sale.UserId).ToList();

        var sharedCodes = roster
            .SelectMany(pair => pair.Value.Select(row => row.Code))
            .GroupBy(code => code, StringComparer.OrdinalIgnoreCase)
            .Count(group => group.Count() > 1);

        var earliest = sales.Concat(priorSales)
            .Select(sale => sale.TradingDate)
            .DefaultIfEmpty()
            .Min();

        return new VanSalesCoverageQualityResult(
            SaleCount: sales.Count,
            RepsWithoutOutletAttribution: salesByRep
                .Count(group => group.All(sale => sale.RouteCustomerCode is null)),
            RepsWithoutVisitData: salesByRep.Count(group => !repsWithVisits.Contains(group.Key)),
            RepsWithoutAccount: reps.Values.Count(rep => rep.AccountCode is null),
            SalesWithoutRouteCustomer: sales.Count(sale => sale.RouteCustomerCode is null),
            SalesWithoutRouteDay: sales.Count(sale => !days.ContainsKey(sale.Key)),
            DaysWithoutPlan: days.Values.Count(day => day.PlannedCustomerCount == 0),
            OutletCodesSharedAcrossAccounts: sharedCodes,
            RosterIsLive: true,
            EarliestObservedSale: earliest == default ? null : earliest);
    }

    // ── Row shapes ──────────────────────────────────────────────────────────────

    private sealed record CallRow(
        Guid UserId,
        DateTime TradingDate,
        string CustomerCode,
        DateTime CheckInTime,
        string? LocationSource,
        double? AccuracyMetres,
        bool HasCoordinates,
        bool CapturedOffline);

    private sealed record RepRow(string Username, string? FullName, string? AccountCode)
    {
        public string DisplayName => string.IsNullOrWhiteSpace(FullName) ? Username : FullName;
    }

    private sealed record RosterRow(string Code, string Name, string? Phone, DateTime CapturedOn);

    private sealed record Bucket(string Label, DateTime Start, DateTime End, bool IsPartial);
}
