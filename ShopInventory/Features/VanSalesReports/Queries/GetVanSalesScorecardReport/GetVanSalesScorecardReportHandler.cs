using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.VanSalesReports.Queries.GetVanSalesScorecardReport;

/// <summary>
/// Builds the period scorecard: a league of reps or routes against target, with movement against the
/// preceding period of equal length.
/// </summary>
/// <remarks>
/// The two windows are loaded through one method and reduced through one method, so the comparison
/// is between two readings of the same instrument. A prior period assembled by a second code path
/// would eventually answer a slightly different question, and a movement between two different
/// questions is worse than no movement at all — it looks like news.
///
/// Nothing here computes a measure. Calls, productive calls, outlets, kilometres and money all come
/// from <see cref="VanSalesMeasures"/>, and new outlets are counted the way the performance report
/// counts them. A roll-up that derives its own figures is a fifth opinion pretending to be a
/// summary of four.
/// </remarks>
public sealed class GetVanSalesScorecardReportHandler(
    ApplicationDbContext db
) : IRequestHandler<GetVanSalesScorecardReportQuery, ErrorOr<VanSalesScorecardReportResult>>
{
    /// <summary>Where sales land when nothing says which route they were made on.</summary>
    private const string NoRouteKey = "«no departure record»";

    public async Task<ErrorOr<VanSalesScorecardReportResult>> Handle(
        GetVanSalesScorecardReportQuery query,
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
                $"A van sales report covers at most {VanSalesFacts.MaximumDays} days.");
        }

        if (query.CallComplianceTarget is <= 0 or > 1 || query.StrikeRateTarget is <= 0 or > 1)
        {
            return Error.Validation(
                "VanSalesReports.InvalidTarget",
                "A target is a rate between 0 and 1.");
        }

        // The window immediately before this one, of the same length. Inclusive dates, so a one-day
        // report compares against the single preceding day rather than against nothing.
        var length = (to - from).Days;
        var priorTo = from.AddDays(-1);
        var priorFrom = priorTo.AddDays(-length);

        var current = await LoadWindowAsync(query, from, to, cancellationToken);
        var prior = await LoadWindowAsync(query, priorFrom, priorTo, cancellationToken);

        var names = await LoadUserNamesAsync(
            current.Sales.Select(sale => sale.UserId)
                .Concat(prior.Sales.Select(sale => sale.UserId))
                .Distinct(),
            cancellationToken);

        var rows = BuildRows(query, current, prior, names);

        return new VanSalesScorecardReportResult(
            FromDate: from,
            ToDate: to,
            PriorFromDate: priorFrom,
            PriorToDate: priorTo,
            Grouping: query.Grouping,
            CallComplianceTarget: query.CallComplianceTarget,
            StrikeRateTarget: query.StrikeRateTarget,
            Summary: BuildSummary(rows, current, prior),
            Rows: rows,
            TakingsMovement: BuildTakingsMovement(current, prior),
            Quality: BuildQuality(rows, current, prior));
    }

    // ── Reads ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Everything one window needs, loaded the same way for both windows.
    /// </summary>
    private async Task<Window> LoadWindowAsync(
        GetVanSalesScorecardReportQuery query,
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken)
    {
        var filter = new VanSalesFactFilter(from, to, query.UserId);

        var sales = await VanSalesFactReader.LoadSalesAsync(db, filter, cancellationToken);
        var days = await LoadRouteDaysAsync(query, from, to, cancellationToken);
        var visits = await LoadVisitsAsync(query, from, to, cancellationToken);
        var newOutlets = await LoadNewOutletsAsync(query, from, to, cancellationToken);

        return new Window(from, to, sales, days, visits, newOutlets);
    }

    private async Task<Dictionary<VanSalesDayKey, VanRouteDayEntity>> LoadRouteDaysAsync(
        GetVanSalesScorecardReportQuery query,
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
    /// Distinct shops called on per rep-day. The channel is pinned and is never a parameter — a
    /// query that takes a channel belongs in the merchandiser feature folder.
    /// </summary>
    private async Task<Dictionary<VanSalesDayKey, HashSet<string>>> LoadVisitsAsync(
        GetVanSalesScorecardReportQuery query,
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
                group => group
                    .Select(entry => entry.CustomerCode)
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .Select(code => code!.Trim())
                    .ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Shops captured in the window, per rep — the performance report's definition, not a new one.
    /// A shop is new when its route-customer record was created here, which is the only thing the
    /// data records about when a rep first put it on the books.
    /// </summary>
    private async Task<Dictionary<Guid, HashSet<string>>> LoadNewOutletsAsync(
        GetVanSalesScorecardReportQuery query,
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken)
    {
        var (windowStartUtc, windowEndUtc) = VanSalesFacts.ToUtcWindow(from, to);

        var queryable = db.RouteCustomers
            .AsNoTracking()
            .Where(customer => customer.CreatedByUserId != null
                               && customer.CreatedAt >= windowStartUtc
                               && customer.CreatedAt < windowEndUtc);

        if (query.UserId.HasValue)
        {
            queryable = queryable.Where(customer => customer.CreatedByUserId == query.UserId.Value);
        }

        var created = await queryable
            .Select(customer => new { customer.CreatedByUserId, customer.Code })
            .ToListAsync(cancellationToken);

        return created
            .GroupBy(row => row.CreatedByUserId!.Value)
            .ToDictionary(
                group => group.Key,
                group => group.Select(row => row.Code).ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    private async Task<Dictionary<Guid, VanSalesMeasures.UserName>> LoadUserNamesAsync(
        IEnumerable<Guid> userIds,
        CancellationToken cancellationToken)
    {
        var ids = userIds.Distinct().ToList();

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

    // ── The league ──────────────────────────────────────────────────────────────

    private static List<VanSalesScorecardRowResult> BuildRows(
        GetVanSalesScorecardReportQuery query,
        Window current,
        Window prior,
        Dictionary<Guid, VanSalesMeasures.UserName> names) =>
        query.Grouping == VanSalesScorecardGrouping.Rep
            ? BuildRepRows(query, current, prior, names)
            : BuildRouteRows(query, current, prior);

    private static List<VanSalesScorecardRowResult> BuildRepRows(
        GetVanSalesScorecardReportQuery query,
        Window current,
        Window prior,
        Dictionary<Guid, VanSalesMeasures.UserName> names)
    {
        // Union of both windows: a rep who traded last period and not this one is the row a
        // scorecard most needs to show, and grouping the current window alone would drop them.
        var userIds = current.Sales.Select(sale => sale.UserId)
            .Concat(prior.Sales.Select(sale => sale.UserId))
            .Concat(current.Days.Keys.Select(key => key.UserId))
            .Distinct()
            .ToList();

        return userIds
            .Select(userId =>
            {
                var now = current.ForRep(userId);
                var then = prior.ForRep(userId);
                var name = names.GetValueOrDefault(userId);

                return BuildRow(
                    query,
                    key: userId.ToString(),
                    label: name is null
                        ? userId.ToString()
                        : string.IsNullOrWhiteSpace(name.FullName) ? name.Username : name.FullName,
                    subLabel: RouteLabelOf(now),
                    userId: userId,
                    now,
                    then);
            })
            .OrderBy(row => row.Band == VanSalesScorecardBand.Unrated)
            .ThenByDescending(row => row.Band)
            .ThenBy(row => row.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// One row per route.
    /// </summary>
    /// <remarks>
    /// A sale whose rep never opened a departure record carries nothing that says which route it was
    /// made on, so it cannot be attributed to one. It goes into its own row rather than being
    /// dropped: the money is real and hiding it would make the route rows add up to less than the
    /// fleet with nothing on the page to explain the difference.
    /// </remarks>
    private static List<VanSalesScorecardRowResult> BuildRouteRows(
        GetVanSalesScorecardReportQuery query,
        Window current,
        Window prior)
    {
        var routeKeys = current.RouteKeys().Concat(prior.RouteKeys()).Distinct(StringComparer.OrdinalIgnoreCase);

        return routeKeys
            .Select(routeKey =>
            {
                var now = current.ForRoute(routeKey);
                var then = prior.ForRoute(routeKey);

                var named = now.Days.Values.FirstOrDefault(day => !string.IsNullOrWhiteSpace(day.RouteName));

                return BuildRow(
                    query,
                    key: routeKey,
                    label: routeKey == NoRouteKey
                        ? "No departure record"
                        : named?.RouteName ?? routeKey,
                    subLabel: routeKey == NoRouteKey
                        ? "Nothing on these sales says which route they were made on"
                        : named?.Territory,
                    userId: null,
                    now,
                    then);
            })
            .OrderBy(row => row.Key == NoRouteKey)
            .ThenBy(row => row.Band == VanSalesScorecardBand.Unrated)
            .ThenByDescending(row => row.Band)
            .ThenBy(row => row.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static VanSalesScorecardRowResult BuildRow(
        GetVanSalesScorecardReportQuery query,
        string key,
        string label,
        string? subLabel,
        Guid? userId,
        Slice now,
        Slice then) =>
        new(
            Key: key,
            Label: label,
            SubLabel: subLabel,
            UserId: userId,
            CallComplianceTarget: query.CallComplianceTarget,
            StrikeRateTarget: query.StrikeRateTarget,
            TradingDays: now.TradingDays,
            Calls: now.Calls,
            CallsAgainstPlan: now.CallsAgainstPlan,
            PlannedCalls: now.PlannedCalls,
            ProductiveCalls: VanSalesMeasures.CountProductiveCalls(now.Sales),
            OutletsBought: VanSalesMeasures.CountOutletsThatBought(now.Sales),
            NewOutlets: now.NewOutlets,
            Kilometres: VanSalesMeasures.SumKilometres(now.Days.Values),
            SalesWithoutTender: now.Sales.Count(sale => string.IsNullOrWhiteSpace(sale.PaymentMethod)),
            SalesWithoutOutlet: now.Sales.Count(sale => sale.Outlet is null),
            TakingsByCurrency: VanSalesMeasures.MoneyByCurrency(now.Sales),
            PriorCalls: then.Calls,
            PriorCallsAgainstPlan: then.CallsAgainstPlan,
            PriorPlannedCalls: then.PlannedCalls,
            PriorProductiveCalls: VanSalesMeasures.CountProductiveCalls(then.Sales),
            PriorOutletsBought: VanSalesMeasures.CountOutletsThatBought(then.Sales),
            PriorTakingsByCurrency: VanSalesMeasures.MoneyByCurrency(then.Sales));

    /// <summary>The route a rep worked most in the window, which is what a rep row is subtitled with.</summary>
    private static string? RouteLabelOf(Slice slice) =>
        slice.Days.Values
            .Where(day => !string.IsNullOrWhiteSpace(day.RouteName))
            .GroupBy(day => day.RouteName!)
            .OrderByDescending(group => group.Count())
            .Select(group => group.Key)
            .FirstOrDefault();

    // ── Summary and quality ─────────────────────────────────────────────────────

    private static VanSalesScorecardSummaryResult BuildSummary(
        List<VanSalesScorecardRowResult> rows,
        Window current,
        Window prior)
    {
        var now = current.All();
        var then = prior.All();

        return new VanSalesScorecardSummaryResult(
            RowCount: rows.Count,
            GreenCount: rows.Count(row => row.Band == VanSalesScorecardBand.Green),
            AmberCount: rows.Count(row => row.Band == VanSalesScorecardBand.Amber),
            RedCount: rows.Count(row => row.Band == VanSalesScorecardBand.Red),
            UnratedCount: rows.Count(row => row.Band == VanSalesScorecardBand.Unrated),
            TradingDays: now.TradingDays,
            Calls: now.Calls,
            CallsAgainstPlan: now.CallsAgainstPlan,
            PlannedCalls: now.PlannedCalls,
            ProductiveCalls: VanSalesMeasures.CountProductiveCalls(now.Sales),
            OutletsBought: VanSalesMeasures.CountOutletsThatBought(now.Sales),
            NewOutlets: now.NewOutlets,
            Kilometres: VanSalesMeasures.SumKilometres(now.Days.Values),
            PriorCalls: then.Calls,
            PriorCallsAgainstPlan: then.CallsAgainstPlan,
            PriorPlannedCalls: then.PlannedCalls,
            PriorProductiveCalls: VanSalesMeasures.CountProductiveCalls(then.Sales),
            PriorOutletsBought: VanSalesMeasures.CountOutletsThatBought(then.Sales),
            TakingsByCurrency: VanSalesMeasures.MoneyByCurrency(now.Sales));
    }

    private static List<VanSalesScorecardMovementResult> BuildTakingsMovement(Window current, Window prior)
    {
        var now = VanSalesMeasures.MoneyByCurrency(current.Sales);
        var then = VanSalesMeasures.MoneyByCurrency(prior.Sales);

        return now.Select(row => row.Currency)
            .Concat(then.Select(row => row.Currency))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(currency => new VanSalesScorecardMovementResult(
                Currency: currency,
                Gross: now.FirstOrDefault(row =>
                    string.Equals(row.Currency, currency, StringComparison.OrdinalIgnoreCase))?.Gross,
                PriorGross: then.FirstOrDefault(row =>
                    string.Equals(row.Currency, currency, StringComparison.OrdinalIgnoreCase))?.Gross))
            .OrderByDescending(row => row.Gross ?? 0)
            .ThenBy(row => row.Currency, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static VanSalesScorecardQualityResult BuildQuality(
        List<VanSalesScorecardRowResult> rows,
        Window current,
        Window prior) =>
        new(
            RowCount: rows.Count,
            UnratedRows: rows.Count(row => row.Band == VanSalesScorecardBand.Unrated),
            RowsWithNoPriorPeriod: rows.Count(row =>
                row.PriorTakingsByCurrency.Count == 0 && row.PriorCalls is null),
            RowsWithNoPlan: rows.Count(row => row.PlannedCalls is null or 0),
            SalesWithoutTender: current.Sales.Count(sale => string.IsNullOrWhiteSpace(sale.PaymentMethod)),
            SalesWithoutOutlet: current.Sales.Count(sale => sale.Outlet is null),
            PriorPeriodEmpty: prior.Sales.Count == 0 && prior.Days.Count == 0);

    // ── One window, and the slices of it a row is built from ────────────────────

    /// <summary>
    /// Everything one period holds. Both the reported window and the one before it are this type, so
    /// a movement is a difference between two identically-built readings.
    /// </summary>
    private sealed record Window(
        DateTime From,
        DateTime To,
        List<VanSaleFact> Sales,
        Dictionary<VanSalesDayKey, VanRouteDayEntity> Days,
        Dictionary<VanSalesDayKey, HashSet<string>> Visits,
        Dictionary<Guid, HashSet<string>> NewOutlets)
    {
        public Slice All() =>
            Slice.Of(Sales, Days, Visits, NewOutlets.Sum(pair => pair.Value.Count));

        public Slice ForRep(Guid userId) =>
            Slice.Of(
                Sales.Where(sale => sale.UserId == userId).ToList(),
                Days.Where(pair => pair.Key.UserId == userId).ToDictionary(pair => pair.Key, pair => pair.Value),
                Visits,
                NewOutlets.TryGetValue(userId, out var captured) ? captured.Count : 0);

        public IEnumerable<string> RouteKeys() =>
            Days.Values
                .Select(day => string.IsNullOrWhiteSpace(day.RouteCode) ? NoRouteKey : day.RouteCode.Trim())
                .Concat(Sales.Where(sale => !Days.ContainsKey(sale.Key)).Select(_ => NoRouteKey))
                .Distinct(StringComparer.OrdinalIgnoreCase);

        public Slice ForRoute(string routeKey)
        {
            var days = Days
                .Where(pair => KeyOf(pair.Value) == routeKey
                               || string.Equals(KeyOf(pair.Value), routeKey, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(pair => pair.Key, pair => pair.Value);

            var sales = Sales
                .Where(sale => Days.TryGetValue(sale.Key, out var day)
                    ? string.Equals(KeyOf(day), routeKey, StringComparison.OrdinalIgnoreCase)
                    : routeKey == NoRouteKey)
                .ToList();

            // New outlets belong to the reps who worked this route in this window. A rep who moved
            // route mid-period contributes their whole capture to both, which is why the figure is
            // reported per route rather than summed to a fleet total from these rows.
            var reps = days.Keys.Select(key => key.UserId).ToHashSet();
            var captured = NewOutlets.Where(pair => reps.Contains(pair.Key)).Sum(pair => pair.Value.Count);

            return Slice.Of(sales, days, Visits, captured);
        }

        private static string KeyOf(VanRouteDayEntity day) =>
            string.IsNullOrWhiteSpace(day.RouteCode) ? NoRouteKey : day.RouteCode.Trim();
    }

    /// <summary>One row's share of one window, with its counters already reduced.</summary>
    private sealed record Slice(
        List<VanSaleFact> Sales,
        Dictionary<VanSalesDayKey, VanRouteDayEntity> Days,
        int TradingDays,
        int? Calls,
        int? CallsAgainstPlan,
        int? PlannedCalls,
        int NewOutlets)
    {
        public static Slice Of(
            List<VanSaleFact> sales,
            Dictionary<VanSalesDayKey, VanRouteDayEntity> days,
            Dictionary<VanSalesDayKey, HashSet<string>> visits,
            int newOutlets)
        {
            var dayKeys = sales.Select(sale => sale.Key).Concat(days.Keys).Distinct().ToList();

            // A day whose plan reads zero is the handset's failed count, not a plan of none. It is
            // out of the denominator, so its calls must be out of the numerator too.
            var planned = days.Values.Where(day => day.PlannedCustomerCount > 0).ToList();

            var plannedKeys = planned
                .Select(day => new VanSalesDayKey(day.UserId, day.TradingDate))
                .ToList();

            return new Slice(
                Sales: sales,
                Days: days,
                TradingDays: dayKeys.Select(key => key.TradingDate).Distinct().Count(),
                Calls: VanSalesMeasures.CountCalls(dayKeys, visits),
                CallsAgainstPlan: planned.Count == 0
                    ? null
                    : VanSalesMeasures.CountCalls(plannedKeys, visits),
                PlannedCalls: planned.Count == 0 ? null : planned.Sum(day => day.PlannedCustomerCount),
                NewOutlets: newOutlets);
        }
    }
}
