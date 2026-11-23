using System.Globalization;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

/// <summary>
/// Builds the van sales management report out of the month's truck report and the product and breakage
/// figures beside it.
/// </summary>
/// <remarks>
/// <para>
/// The office's "Vansales Daily Report" workbook did this with formulas over a sheet of typed rows. Every
/// figure here is one of those formulas, read from the system instead: the insights, the payment summary,
/// the daily and weekly views, the route ranking, the 5 litre columns and the breakages log.
/// </para>
/// <para>
/// Two of the workbook's rules are kept on purpose. A truck's month target is its daily target for every
/// Monday to Friday in the month, as the truck report builds it. And a blank is "not reported", never a
/// zero: a truck-day with no trace is counted as missing, not as a day that sold nothing.
/// </para>
/// </remarks>
public static class VanManagementReportBuilder
{
    private static readonly CultureInfo Money = CultureInfo.GetCultureInfo("en-US");

    public static VanManagementReport Build(
        VanDailyTruckReportResponse truck,
        VanManagementExtrasResponse? extras,
        string? productGroupName)
    {
        var month = new DateTime(truck.FromDate.Year, truck.FromDate.Month, 1);
        var reported = truck.Days.Where(day => day.HasActivity).ToList();
        var reportDate = reported.Count > 0 ? reported.Max(day => day.TradingDate).Date : month;

        var ranked = truck.Trucks
            .OrderByDescending(row => row.Sales)
            .ThenBy(row => row.RouteName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // The run-rate is over the working days from the first to the last day anyone reported, as the
        // workbook's NETWORKDAYS was: a quiet Monday counts against it, the days still to come do not.
        var workingDaysToReportDate = reported.Count == 0
            ? 0
            : Enumerable.Range(0, (reportDate - truck.FromDate.Date).Days + 1)
                .Select(offset => truck.FromDate.Date.AddDays(offset))
                .Count(IsWorkingDay);

        decimal? runRate = workingDaysToReportDate > 0 ? truck.Total.Sales / workingDaysToReportDate : null;
        var daysLeft = Math.Max(0, truck.WorkingDays - workingDaysToReportDate);

        decimal? required = truck.Total.BalanceToTarget is { } balance && balance > 0 && daysLeft > 0
            ? balance / daysLeft
            : null;

        var payments = new VanManagementPayments(
            truck.Days.Sum(day => day.Sales),
            truck.Days.Sum(day => day.Ecocash),
            truck.Days.Sum(day => day.Cash),
            truck.Days.Sum(day => day.Innbucks),
            truck.Days.Sum(day => day.OtherTender),
            truck.Days.Sum(day => day.Untendered),
            truck.Days.Sum(day => day.AccountSales ?? 0m));

        var report = new VanManagementReport(month, reportDate, truck, productGroupName, extras)
        {
            Ranked = ranked,
            WorkingDaysToReportDate = workingDaysToReportDate,
            DailyRunRate = runRate,
            RequiredPerRemainingDay = required,
            Payments = payments,
            ExceptionChecks = ExceptionChecks(truck),
            OperatingIssues = truck.Days
                .Where(day => !string.IsNullOrWhiteSpace(day.Notes))
                .OrderByDescending(day => day.TradingDate)
                .ThenBy(day => day.RouteName, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Dates = Enumerable.Range(0, (truck.ToDate.Date - truck.FromDate.Date).Days + 1)
                .Select(offset => truck.FromDate.Date.AddDays(offset))
                .ToList(),
            Weeks = Weeks(truck),
            Routes = RouteRanking(truck),
            Products = Products(truck, extras, reportDate),
            Breakages = Breakages(truck, extras)
        };

        return report with { Insights = Insights(report) };
    }

    /// <summary>
    /// Names each truck by the SAP business partner it sells on — "Van Sales Up Country 2 - Bulawayo"
    /// rather than the route's own name — on its summary and on every one of its days, so the tables,
    /// the charts and the Monthly Summary and Raw Data sheets all say the same name.
    /// </summary>
    /// <remarks>
    /// A truck whose account has no name in <paramref name="partnerNames"/> (no account, or the partner
    /// cache could not be read) keeps its route name: a truck must never be left unnamed. The route code
    /// is untouched, so the two can still be told apart.
    /// </remarks>
    /// <returns>How many trucks were renamed.</returns>
    public static int NameTrucksByBusinessPartner(
        VanDailyTruckReportResponse truck,
        IReadOnlyDictionary<string, string> partnerNames)
    {
        var renamed = new Dictionary<int, string>();

        foreach (var summary in truck.Trucks)
        {
            if (summary.RouteId is { } routeId
                && summary.BusinessPartnerCode is { Length: > 0 } code
                && partnerNames.TryGetValue(code.Trim(), out var name)
                && !string.IsNullOrWhiteSpace(name))
            {
                summary.RouteName = name.Trim();
                renamed[routeId] = summary.RouteName;
            }
        }

        foreach (var day in truck.Days)
        {
            if (renamed.TryGetValue(day.RouteId, out var name))
            {
                day.RouteName = name;
            }
        }

        return renamed.Count;
    }

    /// <summary>
    /// Every truck on one day, with its month to that day: the workbook's daily management view and its
    /// truck cards. A truck that has no row on the day is still listed, with no day.
    /// </summary>
    public static IReadOnlyList<VanManagementTruckDay> DayView(VanManagementReport report, DateTime date)
    {
        var day = date.Date;

        return report.Trucks
            .Select(truck =>
            {
                var days = report.Truck.Days
                    .Where(row => row.RouteId == truck.RouteId && row.TradingDate.Date <= day)
                    .ToList();

                return new VanManagementTruckDay(
                    truck,
                    days.FirstOrDefault(row => row.TradingDate.Date == day),
                    ToDate(days, truck.PeriodTarget));
            })
            .ToList();
    }

    /// <summary>A truck's month up to a day, measured the way the truck report measures the month.</summary>
    public static VanManagementToDate ToDate(IReadOnlyCollection<VanDailyTruckDay> days, decimal? periodTarget)
    {
        // The CCR is over the days with a round where any has one; the list days are left out of it then,
        // as the API does for the period.
        var planned = days.Where(day => day.PlannedCustomers is > 0).ToList();
        var measured = planned.Any(day => !day.PlanIsWholeList)
            ? planned.Where(day => !day.PlanIsWholeList).ToList()
            : planned;

        var withCheckIns = days.Where(day => day.CustomersVisited > 0).ToList();
        var plannedCustomers = measured.Sum(day => day.PlannedCustomers!.Value);
        var callsMade = withCheckIns.Sum(day => day.CallsMade);

        return new VanManagementToDate(
            days.Sum(day => day.Sales),
            days.Sum(day => day.Ecocash),
            days.Sum(day => day.AccountSales ?? 0m),
            days.Sum(day => day.CustomersVisited),
            days.Sum(day => day.ProductiveCalls),
            days.Sum(day => day.NewCustomers),
            days.Sum(day => day.Kilometres ?? 0),
            days.Sum(day => day.FuelLitres ?? 0m),
            plannedCustomers > 0 ? (double)measured.Sum(day => day.PlannedCallsMade) / plannedCustomers : null,
            callsMade > 0 ? (double)withCheckIns.Sum(day => day.ProductiveCalls) / callsMade : null,
            periodTarget);
    }

    /// <summary>The week holding <paramref name="date"/>, or the latest week with any report, or the first.</summary>
    public static VanManagementWeek? WeekOf(VanManagementReport report, DateTime? date) =>
        (date is { } day ? report.Weeks.FirstOrDefault(week => week.Start <= day.Date && day.Date <= week.End) : null)
        ?? report.Weeks.LastOrDefault(week => week.HasData)
        ?? report.Weeks.FirstOrDefault();

    // — The pieces ————————————————————————————————————————————————————————————————————————————

    private static IReadOnlyList<VanManagementException> ExceptionChecks(VanDailyTruckReportResponse truck)
    {
        var counts = truck.Exceptions;

        return
        [
            new("Working days a truck reported nothing", counts.NotReported, "No Start Day, call or sale on a Monday to Friday that has passed."),
            new("Reported days below the daily target", counts.BelowTarget, "A finished working day with takings under the truck's daily target."),
            new("Sales with no customers checked into", truck.Days.Count(day => day.Sales > 0 && day.CustomersVisited == 0), "Takings recorded on a day no shop was checked into, so CCR and PCR cannot be read."),
            new("Distance driven with no sales", counts.DistanceWithoutSales, "Kilometres on the odometer and nothing sold."),
            new("Mileage records needing review", counts.MileageNeedsReview, "One odometer reading without the other, or a closing reading below the opening one."),
            new("Days closed a day late", counts.ReturnedLate, "End Day was tapped on a later date, so the time in is not when the truck got back."),
            new("Reported days with no fuel entered", truck.Days.Count(day => day.IsComplete && day.IsWorkingDay && day.HasActivity && day.FuelLitres is null), "The office has not entered the litres issued. Only a problem on a fuel day.")
        ];
    }

    private static IReadOnlyList<VanManagementWeek> Weeks(VanDailyTruckReportResponse truck)
    {
        var from = truck.FromDate.Date;
        var to = truck.ToDate.Date;
        var weeks = new List<VanManagementWeek>();

        // Monday to Sunday, the first and last clipped to the month as the workbook's week index is.
        var start = from;
        while (start <= to)
        {
            var daysToSunday = ((int)DayOfWeek.Sunday - (int)start.DayOfWeek + 7) % 7;
            var end = start.AddDays(daysToSunday);
            if (end > to)
            {
                end = to;
            }

            var inWeek = truck.Days.Where(day => day.TradingDate.Date >= start && day.TradingDate.Date <= end).ToList();
            var weekSales = inWeek.Sum(day => day.Sales);

            var trucks = truck.Trucks
                .Select(row =>
                {
                    var mine = inWeek.Where(day => day.RouteId == row.RouteId).ToList();
                    var sales = mine.Sum(day => day.Sales);

                    return new VanManagementWeekTruck(
                        row.RouteId ?? 0,
                        row.RouteName,
                        sales,
                        mine.Count(day => day.HasActivity),
                        mine.Sum(day => day.AccountSales ?? 0m),
                        weekSales > 0 ? (double)(sales / weekSales) : null);
                })
                .ToList();

            var days = Enumerable.Range(0, (end - start).Days + 1)
                .Select(offset => start.AddDays(offset))
                .Select(date =>
                {
                    var onDay = inWeek.Where(day => day.TradingDate.Date == date).ToList();
                    return new VanManagementWeekDay(date, onDay.Sum(day => day.Sales), onDay.Count(day => day.HasActivity));
                })
                .ToList();

            weeks.Add(new VanManagementWeek(start, end, trucks, days));
            start = end.AddDays(1);
        }

        return weeks;
    }

    private static VanManagementRouteRanking RouteRanking(VanDailyTruckReportResponse truck)
    {
        var reported = truck.Days.Where(day => day.HasActivity).ToList();
        var withArea = reported.Where(day => !string.IsNullOrWhiteSpace(day.AreaName)).ToList();
        var withoutArea = reported.Where(day => string.IsNullOrWhiteSpace(day.AreaName)).ToList();

        // Trimmed and case folded, so "Guruve" and "guruve " are one area; spellings that differ stay apart.
        var ranked = withArea
            .GroupBy(day => day.AreaName!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(area => new VanManagementRouteSales(
                area.First().AreaName!.Trim(),
                area.Sum(day => day.Sales),
                area.Count(),
                string.Join(", ", area.Select(day => day.RouteName).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))))
            .OrderByDescending(area => area.Sales)
            .ThenBy(area => area.Area, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new VanManagementRouteRanking(ranked, reported.Count, withoutArea.Count, withoutArea.Sum(day => day.Sales));
    }

    private static VanManagementProducts Products(VanDailyTruckReportResponse truck, VanManagementExtrasResponse? extras, DateTime reportDate)
    {
        if (extras is null)
        {
            return VanManagementProducts.Empty;
        }

        var trucks = truck.Trucks
            .Select(row =>
            {
                var mine = extras.Products.Where(product => product.RouteId == row.RouteId).ToList();
                return new VanManagementProductTruck(row.RouteId ?? 0, row.RouteName, mine.Sum(product => product.Units), mine.Sum(product => product.Value));
            })
            .ToList();

        var lastDay = extras.Products.Count > 0
            ? new[] { reportDate, extras.Products.Max(product => product.TradingDate.Date) }.Max()
            : reportDate;

        var days = Enumerable.Range(0, Math.Max(0, (lastDay - truck.FromDate.Date).Days + 1))
            .Select(offset => truck.FromDate.Date.AddDays(offset))
            .Select(date =>
            {
                var onDay = extras.Products.Where(product => product.TradingDate.Date == date).ToList();
                return new VanManagementProductDay(date, onDay.Sum(product => product.Units), onDay.Sum(product => product.Value));
            })
            .ToList();

        var items = extras.Products
            .GroupBy(product => product.ItemCode, StringComparer.OrdinalIgnoreCase)
            .Select(item => new VanManagementProductItem(
                item.Key,
                item.Select(product => product.ItemName).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)),
                item.Sum(product => product.Units),
                item.Sum(product => product.Value)))
            .OrderByDescending(item => item.Units)
            .ThenBy(item => item.ItemCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new VanManagementProducts(trucks, days, items, extras.ProductUnitsOutsideRoutes);
    }

    private static VanManagementBreakages Breakages(VanDailyTruckReportResponse truck, VanManagementExtrasResponse? extras)
    {
        if (extras is null)
        {
            return VanManagementBreakages.Empty;
        }

        var lines = extras.Breakages;
        var known = truck.Trucks.Select(row => row.RouteId).ToHashSet();

        var trucks = truck.Trucks
            .Select(row => Truck(row.RouteId, row.RouteName, lines.Where(line => line.RouteId == row.RouteId).ToList()))
            .ToList();

        // A rep on no route, or on a route no longer listed, is not lost: the workbook kept an
        // "unassigned truck" row for the same reason.
        var unassigned = lines.Where(line => !known.Contains(line.RouteId)).ToList();
        if (unassigned.Count > 0)
        {
            trucks.Add(Truck(null, "No route", unassigned));
        }

        var causes = lines
            .GroupBy(line => string.IsNullOrWhiteSpace(line.Reason) ? "No cause given" : line.Reason!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(cause => new VanManagementBreakageCause(
                cause.First().Reason?.Trim() is { Length: > 0 } named ? named : "No cause given",
                cause.Count(),
                cause.Sum(line => line.Units),
                cause.Sum(line => line.ValueBeforeVat ?? 0m)))
            .OrderByDescending(cause => cause.Units)
            .ThenBy(cause => cause.Cause, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new VanManagementBreakages(trucks, causes, lines.OrderBy(line => line.TradingDate).ThenBy(line => line.BreakageId).ToList());

        static VanManagementBreakageTruck Truck(int? routeId, string name, IReadOnlyCollection<VanBreakageLine> mine) =>
            new(routeId, name, mine.Count, mine.Sum(line => line.Units), mine.Sum(line => line.ValueBeforeVat ?? 0m));
    }

    /// <summary>
    /// The workbook's numbered findings, with its wording where the figures allow it. Each is left out
    /// when there is nothing under it rather than written over zeros.
    /// </summary>
    private static IReadOnlyList<VanManagementInsight> Insights(VanManagementReport report)
    {
        var insights = new List<VanManagementInsight>();
        var total = report.Total;

        if (total.Sales <= 0)
        {
            insights.Add(new("Awaiting sales", $"No van sales are recorded for {report.Month:MMMM yyyy} yet."));
            return insights;
        }

        // 1 — the pace against target.
        if (total.PeriodTarget is > 0 && report.RequiredPerRemainingDay is { } required && report.DailyRunRate is { } runRate && runRate > 0)
        {
            var gap = (double)(required / runRate) - 1;
            var title = gap > 0 ? "Target pace needs to accelerate" : "On pace for the month's target";
            var direction = gap > 0 ? "above" : "below";

            insights.Add(new(title,
                $"Sales are {Percent(total.TargetAchieved)} of the {Dollars(total.PeriodTarget)} target. The remaining {Dollars(total.BalanceToTarget)} " +
                $"requires {Dollars(required)} per remaining working day — {Math.Abs(gap):P0} {direction} the recorded working-day run-rate of {Dollars(runRate)}."));
        }
        else if (total.PeriodTarget is > 0)
        {
            insights.Add(new(total.BalanceToTarget <= 0 ? "Target reached" : "The month's target",
                total.BalanceToTarget <= 0
                    ? $"Sales of {Dollars(total.Sales)} have passed the {Dollars(total.PeriodTarget)} target by {Dollars(-total.BalanceToTarget)}."
                    : $"Sales are {Percent(total.TargetAchieved)} of the {Dollars(total.PeriodTarget)} target, with {Dollars(total.BalanceToTarget)} still to sell."));
        }

        // 2 — the leaders.
        var top = report.Ranked.Where(truck => truck.Sales > 0).Take(3).ToList();
        if (top.Count >= 2)
        {
            var best = top.Where(truck => truck.TargetAchieved.HasValue).OrderByDescending(truck => truck.TargetAchieved).FirstOrDefault();
            var body = $"The top {top.Count} contribute {(double)(top.Sum(truck => truck.Sales) / total.Sales):P1} of total van sales. " +
                       $"{top[0].RouteName} leads {top[1].RouteName} by {Cents(top[0].Sales - top[1].Sales)}";
            body += best is not null
                ? $"; the strongest target achievement among them is {best.RouteName} ({Percent(best.TargetAchieved)})."
                : ".";

            insights.Add(new($"{top[0].RouteName} leads van sales", body));
        }

        // 3 — distance.
        var perKm = report.Trucks.Where(truck => truck.SalesPerKilometre is > 0).OrderByDescending(truck => truck.SalesPerKilometre).ToList();
        if (total.Kilometres > 0 && perKm.Count > 0)
        {
            var body = $"The fleet travelled {total.Kilometres:N0} km" +
                       (total.KilometresPerLitre is { } kmL ? $" at {kmL:N2} km a litre" : string.Empty) + ". ";
            body += perKm.Count > 1
                ? $"{perKm[0].RouteName} has the highest sales per km at {Cents(perKm[0].SalesPerKilometre)}/km; {perKm[^1].RouteName} is lowest at {Cents(perKm[^1].SalesPerKilometre)}/km."
                : $"{perKm[0].RouteName} sold {Cents(perKm[0].SalesPerKilometre)} a km.";

            insights.Add(new("Network distance and productivity", body));
        }

        // 4 — customers.
        var reach = report.Trucks.Where(truck => truck.CustomersVisited > 0).ToList();
        if (reach.Count > 0)
        {
            var most = reach.OrderByDescending(truck => truck.CustomersVisited).First();
            var lowAov = reach.Where(truck => truck.AverageOrderValue is > 0).OrderBy(truck => truck.AverageOrderValue).FirstOrDefault();
            var lowPcr = reach.Where(truck => truck.ProductiveCallRate.HasValue).OrderBy(truck => truck.ProductiveCallRate).FirstOrDefault();

            var body = $"{most.RouteName} checked into {most.CustomersVisited:N0} customers — the most in the network.";
            if (lowAov is not null)
            {
                body += $" {lowAov.RouteName} has the lowest average order at {Cents(lowAov.AverageOrderValue)}";
                body += lowPcr is not null ? ";" : ".";
            }

            if (lowPcr is not null)
            {
                body += $" {lowPcr.RouteName} has the lowest PCR at {Percent(lowPcr.ProductiveCallRate)}, a conversion priority.";
            }

            insights.Add(new("Customer reach is not translating evenly", body.Trim()));
        }

        // 5 — what to do.
        var actions = new List<string>();
        if (total.BalanceToTarget is > 0)
        {
            actions.Add($"close the {Dollars(total.BalanceToTarget)} sales gap");
        }

        if (perKm.Count > 1)
        {
            actions.Add($"review {perKm[^1].RouteName} routes ({Cents(perKm[^1].SalesPerKilometre)} sales/km)");
        }

        if (reach.Where(truck => truck.ProductiveCallRate.HasValue).OrderBy(truck => truck.ProductiveCallRate).FirstOrDefault() is { } convert)
        {
            actions.Add($"investigate {convert.RouteName} customer conversion (PCR {Percent(convert.ProductiveCallRate)})");
        }

        var missing = report.ExceptionChecks.Where(check => check.Count > 0).Sum(check => check.Count);
        if (actions.Count > 0)
        {
            var text = string.Join("; ", actions);
            insights.Add(new("Next actions",
                char.ToUpperInvariant(text[0]) + text[1..] + "." +
                (missing > 0 ? " Check the exception monitor before making route decisions." : string.Empty)));
        }

        return insights;
    }

    private static bool IsWorkingDay(DateTime day) => day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

    private static string Dollars(decimal? amount) => amount is { } value ? value.ToString("$#,##0", Money) : "—";

    private static string Cents(decimal? amount) => amount is { } value ? value.ToString("$#,##0.00", Money) : "—";

    private static string Percent(double? rate) => rate is { } value ? value.ToString("P1", Money) : "—";
}
