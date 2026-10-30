using System.Globalization;
using ShopInventory.Features.VanSalesReports.Queries.GetAdrPerformanceReport;

namespace ShopInventory.Features.VanSalesReports.Queries.GetOrderConversionReport;

/// <summary>
/// The parts of the order conversion report that read the figures rather than count them: where the
/// period is heading, and what a manager should look at. Pure, so they are tested without a database.
/// </summary>
internal static class OrderConversionInsights
{
    /// <summary>The conversion a healthy order book reaches; the upside is measured to it.</summary>
    public const double TargetConversion = 0.90;

    /// <summary>Below this a rep's orders are not arriving often enough to leave alone.</summary>
    public const double WeakConversion = 0.75;

    /// <summary>Fewer orders than this say too little about a rep to single them out.</summary>
    public const int MinimumOrdersToJudge = 5;

    /// <summary>An order this many days old has had its chance to be invoiced.</summary>
    public const int SettleDays = 2;

    // ── Projection ──────────────────────────────────────────────────────────────

    public static OrderConversionProjectionResult Project(
        IReadOnlyList<AdrConversion.Outcome> outcomes,
        string currency,
        DateTime from,
        DateTime to,
        DateTime todayCat)
    {
        var mine = outcomes.Where(outcome => Same(outcome.Order.Currency, currency)).ToList();
        var end = to < todayCat ? to : todayCat;
        var settledBefore = todayCat.AddDays(-SettleDays);

        // The weekdays the reps actually order on: a fleet that works Saturdays projects Saturdays.
        var orderWeekdays = mine.Select(outcome => outcome.Order.TradingDate.DayOfWeek).ToHashSet();
        var orderDays = Math.Max(1, CountDays(from, end, orderWeekdays));

        var ordered = mine.Sum(outcome => outcome.Ordered);
        var invoiced = mine.Sum(outcome => outcome.Invoiced ?? 0m);

        var settled = mine.Where(outcome => outcome.Order.TradingDate <= settledBefore).ToList();
        var settledOrdered = settled.Sum(outcome => outcome.Ordered);
        double? settledConversion = settledOrdered > 0
            ? (double)(settled.Sum(outcome => outcome.Invoiced ?? 0m) / settledOrdered)
            : null;
        var rate = settledConversion ?? (ordered > 0 ? (double)(invoiced / ordered) : 0d);

        var openValue = mine
            .Where(outcome => outcome.Order.TradingDate > settledBefore && !outcome.IsInvoiced && !outcome.Order.IsWithdrawn)
            .Sum(outcome => outcome.Ordered);
        var expected = ExpectedLate(mine, settledBefore, settledConversion);

        // The horizon: the rest of the month the period ends in, or the next month when it ends on the last day.
        var monthEnd = new DateTime(end.Year, end.Month, DateTime.DaysInMonth(end.Year, end.Month));
        // With under a week of the month left, its remainder is too short to say anything: the
        // horizon is the next month instead, and the month-end figure still covers the remainder.
        var orderedPerDay = ordered / orderDays;
        var restOfMonthDays = CountDays(end.AddDays(1), monthEnd, orderWeekdays);
        var restOfMonthOrdered = Round(orderedPerDay * restOfMonthDays);

        DateTime horizonFrom, horizonTo;
        string label;
        if ((monthEnd - end).TotalDays < 7)
        {
            horizonFrom = monthEnd.AddDays(1);
            horizonTo = horizonFrom.AddMonths(1).AddDays(-1);
            label = horizonFrom.ToString("MMMM yyyy", CultureInfo.InvariantCulture);
        }
        else
        {
            horizonFrom = end.AddDays(1);
            horizonTo = monthEnd;
            label = $"the rest of {end.ToString("MMMM", CultureInfo.InvariantCulture)}";
        }

        var horizonDays = CountDays(horizonFrom, horizonTo, orderWeekdays);
        var projectedOrdered = Round(orderedPerDay * horizonDays);
        var projectedConverted = Round(projectedOrdered * (decimal)rate);

        // A month total only when the period holds the whole of the month so far.
        decimal? monthOrdered = null, monthConverted = null;
        var monthStart = new DateTime(end.Year, end.Month, 1);
        if (end < monthEnd && from <= monthStart)
        {
            var inMonth = mine.Where(outcome => outcome.Order.TradingDate >= monthStart).ToList();
            monthOrdered = Round(inMonth.Sum(outcome => outcome.Ordered) + restOfMonthOrdered);
            monthConverted = Round(inMonth.Sum(outcome => outcome.Invoiced ?? 0m)
                                   + ExpectedLate(inMonth, settledBefore, settledConversion)
                                   + Round(restOfMonthOrdered * (decimal)rate));
        }

        var weeks = mine
            .GroupBy(outcome => WeekStart(outcome.Order.TradingDate))
            .OrderBy(group => group.Key)
            .Select(group => new OrderConversionWeekResult(
                group.Key,
                group.Count(),
                group.Sum(outcome => outcome.Ordered),
                group.Sum(outcome => outcome.Invoiced ?? 0m)))
            .ToList();

        return new OrderConversionProjectionResult(
            Currency: currency,
            OrderDays: orderDays,
            OrderedPerDay: Round(orderedPerDay),
            ConvertedPerDay: Round(invoiced / orderDays),
            SettledConversion: settledConversion,
            OpenOrdersValue: openValue,
            ExpectedFromOpenOrders: expected,
            ProjectedPeriodConversion: ordered > 0 ? (double)((invoiced + expected) / ordered) : null,
            HorizonLabel: label,
            HorizonFrom: horizonFrom,
            HorizonTo: horizonTo,
            HorizonDays: horizonDays,
            ProjectedOrdered: projectedOrdered,
            ProjectedConverted: projectedConverted,
            ProjectedMonthOrdered: monthOrdered,
            ProjectedMonthConverted: monthConverted,
            TargetConversion: TargetConversion,
            UpsideAtTarget: rate >= TargetConversion ? 0m : Round(projectedOrdered * (decimal)(TargetConversion - rate)),
            WeeklyTrendPoints: Trend(weeks.Where(week => week.WeekStart.AddDays(6) <= settledBefore).ToList()),
            Weeks: weeks);
    }

    /// <summary>
    /// What the orders too young to have settled should still bring in: they are expected to end as the
    /// settled ones did, less what they have already sold.
    /// </summary>
    private static decimal ExpectedLate(
        IReadOnlyCollection<AdrConversion.Outcome> outcomes,
        DateTime settledBefore,
        double? settledConversion)
    {
        if (settledConversion is not { } rate)
        {
            return 0m;
        }

        var recent = outcomes.Where(outcome => outcome.Order.TradingDate > settledBefore).ToList();
        var already = recent.Sum(outcome => outcome.Invoiced ?? 0m);
        return Round(Math.Max(0m, recent.Sum(outcome => outcome.Ordered) * (decimal)rate - already));
    }

    /// <summary>The least-squares slope of the weekly conversion, in points a week.</summary>
    internal static double? Trend(IReadOnlyList<OrderConversionWeekResult> weeks)
    {
        var points = weeks
            .Where(week => week.Conversion is not null)
            .Select((week, index) => (X: (double)index, Y: week.Conversion!.Value * 100))
            .ToList();

        if (points.Count < 3)
        {
            return null;
        }

        var meanX = points.Average(point => point.X);
        var meanY = points.Average(point => point.Y);
        var spread = points.Sum(point => (point.X - meanX) * (point.X - meanX));

        return spread == 0
            ? null
            : Math.Round(points.Sum(point => (point.X - meanX) * (point.Y - meanY)) / spread, 2);
    }

    private static int CountDays(DateTime from, DateTime to, IReadOnlySet<DayOfWeek> weekdays)
    {
        var count = 0;
        for (var day = from.Date; day <= to.Date; day = day.AddDays(1))
        {
            if (weekdays.Contains(day.DayOfWeek))
            {
                count++;
            }
        }

        return count;
    }

    internal static DateTime WeekStart(DateTime day) => day.Date.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    // ── What to look at ─────────────────────────────────────────────────────────

    /// <summary>Everything a manager should look at, most pressing first.</summary>
    public static List<OrderConversionAttentionResult> Attention(
        IReadOnlyList<AdrConversion.Outcome> outcomes,
        IReadOnlyList<OrderConversionRepResult> reps,
        IReadOnlyList<OrderConversionRaiserResult> raisers,
        OrderConversionProductsResult products,
        IReadOnlyList<OrderConversionShopResult> shops,
        OrderConversionProjectionResult? projection,
        string? lead,
        DateTime todayCat)
    {
        var found = new List<OrderConversionAttentionResult>();
        if (lead is null)
        {
            return found;
        }

        var mine = outcomes.Where(outcome => Same(outcome.Order.Currency, lead)).ToList();
        var ordered = mine.Sum(outcome => outcome.Ordered);
        if (ordered <= 0)
        {
            return found;
        }

        var settledBefore = todayCat.AddDays(-SettleDays);

        // Orders that reached SAP but no van invoice was ever linked to them.
        var lost = mine
            .Where(outcome => outcome.Kind == AdrOutcomes.NoLinkedInvoice && outcome.Order.TradingDate <= settledBefore)
            .ToList();
        if (lost.Count > 0)
        {
            var value = lost.Sum(outcome => outcome.Ordered);
            found.Add(new(
                Share(value, ordered) >= 0.10 ? "high" : "medium",
                "Orders never invoiced",
                $"{Count(lost.Count, "order")} older than two days {(lost.Count == 1 ? "has" : "have")} no van invoice",
                $"{Share(value, ordered):P0} of the ordered value. They reached SAP but no van invoice is linked: either invoiced "
                + "straight in SAP B1, which bypasses the van app, or never delivered. Ask the reps which, order by order.",
                lead, value, $"orders:{AdrOutcomes.NoLinkedInvoice}"));
        }

        // Orders still on the handset or the queue after a day.
        var stuck = mine
            .Where(outcome => outcome.Kind == AdrOutcomes.NotYetInSap && outcome.Order.TradingDate < todayCat.AddDays(-1))
            .ToList();
        if (stuck.Count > 0)
        {
            var value = stuck.Sum(outcome => outcome.Ordered);
            found.Add(new(
                stuck.Any(outcome => outcome.Order.TradingDate <= settledBefore.AddDays(-1)) ? "high" : "medium",
                "Orders not reaching SAP",
                $"{Count(stuck.Count, "order")} captured over a day ago {(stuck.Count == 1 ? "is" : "are")} still not in SAP",
                "Waiting on the handset to sync, an approval, or a posting failure. A van cannot invoice an order SAP does not have.",
                lead, value, $"orders:{AdrOutcomes.NotYetInSap}"));
        }

        // Short supply, and which products cause it.
        var invoicedOrdered = mine.Where(outcome => outcome.IsInvoiced).Sum(outcome => outcome.Ordered);
        var shortValue = mine.Sum(outcome => outcome.Short);
        if (shortValue > 0 && invoicedOrdered > 0)
        {
            var worst = products.Items
                .Select(item => (Item: item, Short: item.Money.FirstOrDefault(money => Same(money.Currency, lead))?.ShortSupplied ?? 0m))
                .Where(pair => pair.Short > 0)
                .OrderByDescending(pair => pair.Short)
                .Take(3)
                .Select(pair => $"{pair.Item.ItemDescription ?? pair.Item.ItemCode} ({pair.Item.ShortQuantity:N0}{(pair.Item.UoMCode is null ? "" : " " + pair.Item.UoMCode)} short)")
                .ToList();
            var share = Share(shortValue, invoicedOrdered);
            found.Add(new(
                share >= 0.05 ? "high" : share >= 0.02 ? "medium" : "low",
                "Short supply",
                $"Vans delivered {share:P1} less than was ordered on invoiced orders",
                worst.Count == 0
                    ? "Load the vans against the order book."
                    : $"Most short: {string.Join(", ", worst)}. Load the vans against the order book before they leave.",
                lead, shortValue, "items"));
        }

        // Reps whose orders rarely arrive.
        var weak = reps
            .Select(rep => (Rep: rep, Money: rep.Summary.Money.FirstOrDefault(money => Same(money.Currency, lead))))
            .Where(pair => pair.Money is { Conversion: { } conversion } && conversion < WeakConversion
                           && pair.Rep.Summary.Counts.Ordered >= MinimumOrdersToJudge)
            .OrderByDescending(pair => pair.Money!.Gap)
            .ToList();
        if (weak.Count > 0)
        {
            var named = string.Join(", ", weak.Take(3).Select(pair =>
                $"{pair.Rep.DisplayName} ({OrderRaisers.Label(pair.Rep.Role).TrimEnd('s')}, {pair.Money!.Conversion:P0})"));
            found.Add(new(
                weak.Any(pair => pair.Money!.Conversion < 0.6) ? "high" : "medium",
                "Reps below conversion",
                $"{Count(weak.Count, "rep")} converted under {WeakConversion:P0} of what they ordered",
                $"{named}{(weak.Count > 3 ? $" and {weak.Count - 3} more" : "")}. Check whether their orders are real demand, and whether the van serving them carries the stock.",
                lead, weak.Sum(pair => pair.Money!.Gap), "reps"));
        }

        // Sales reps against ADRs.
        var sales = raisers.FirstOrDefault(raiser => raiser.Role == OrderRaisers.SalesReps);
        var adrs = raisers.FirstOrDefault(raiser => raiser.Role == OrderRaisers.Adrs);
        if (sales?.Summary.Money.FirstOrDefault(money => Same(money.Currency, lead)) is { Conversion: { } salesRate }
            && adrs?.Summary.Money.FirstOrDefault(money => Same(money.Currency, lead)) is { Conversion: { } adrRate } adrMoney
            && sales.Summary.Counts.Ordered >= MinimumOrdersToJudge
            && adrs.Summary.Counts.Ordered >= MinimumOrdersToJudge
            && Math.Abs(salesRate - adrRate) >= 0.10)
        {
            var (behind, ahead, behindRate, aheadRate) = adrRate < salesRate
                ? ("ADRs", "sales reps", adrRate, salesRate)
                : ("Sales reps", "ADRs", salesRate, adrRate);
            found.Add(new(
                "medium",
                "Sales reps against ADRs",
                $"{behind} convert {(aheadRate - behindRate) * 100:N0} points below {ahead}",
                $"{behind} {behindRate:P0}, {ahead} {aheadRate:P0}. "
                + (behind == "ADRs"
                    ? "An ADR's order relies on a van to deliver it: check the hand-over from ADR to van."
                    : "Sales reps' orders are delivered later by a van: check those orders reach the van's load."),
                null, null, "reps"));
        }

        // Cancellations.
        var cancelled = mine.Where(outcome => outcome.Kind == AdrOutcomes.Cancelled).ToList();
        var cancelledValue = cancelled.Sum(outcome => outcome.Ordered);
        if (cancelled.Count > 0 && Share(cancelledValue, ordered) >= 0.10)
        {
            found.Add(new(
                "medium",
                "Cancellations",
                $"{Share(cancelledValue, ordered):P0} of ordered value was cancelled or rejected",
                $"{Count(cancelled.Count, "order")}. Frequent cancellations point at credit holds, duplicate orders, or shops changing their minds after the rep left.",
                lead, cancelledValue, $"orders:{AdrOutcomes.Cancelled}"));
        }

        // Invoiced at other prices.
        var repriced = mine.Sum(outcome => outcome.Repriced);
        if (repriced > 0 && Share(repriced, ordered) >= 0.03)
        {
            found.Add(new(
                "low",
                "Price differences",
                $"Invoices came to {repriced:N2} less than the orders' prices",
                "The van invoiced at different prices than the rep ordered at. Check the handsets' price lists and any special prices.",
                lead, repriced, $"orders:{AdrOutcomes.Repriced}"));
        }

        // Shops that order and do not receive.
        var unserved = shops.Where(shop => shop.Orders >= 2 && shop.Invoiced == 0).ToList();
        if (unserved.Count > 0)
        {
            var value = unserved.Sum(shop => shop.Money.FirstOrDefault(money => Same(money.Currency, lead))?.Ordered ?? 0m);
            found.Add(new(
                unserved.Count >= 5 ? "medium" : "low",
                "Shops not being served",
                $"{Count(unserved.Count, "shop")} ordered twice or more and received nothing",
                $"{string.Join(", ", unserved.Take(3).Select(shop => shop.CustomerName ?? shop.CustomerCode))}{(unserved.Count > 3 ? $" and {unserved.Count - 3} more" : "")}. A shop that keeps ordering and never receives will stop ordering.",
                lead, value, "shops"));
        }

        // Slow to invoice.
        var days = mine.Where(outcome => outcome.DaysToInvoice is not null).Select(outcome => outcome.DaysToInvoice!.Value).Order().ToList();
        if (days.Count >= MinimumOrdersToJudge && days[days.Count / 2] > 1.5)
        {
            found.Add(new(
                "medium",
                "Slow to invoice",
                $"Half the orders took more than {days[days.Count / 2]:N1} days to be invoiced",
                "Orders that wait are more likely to be cancelled or bought elsewhere. Check the vans pick orders up on their next visit.",
                null, null, "orders"));
        }

        // Conversion falling week on week.
        if (projection?.WeeklyTrendPoints is { } slope && slope <= -2)
        {
            found.Add(new(
                slope <= -5 ? "high" : "medium",
                "Falling conversion",
                $"Conversion is falling about {Math.Abs(slope):N1} points a week",
                "Measured over the settled weeks of the period. Look at the most recent weeks in the ledger for the reason.",
                null, null, "orders"));
        }

        // Orders captured without prices.
        var unpriced = mine.Count(outcome => !outcome.Order.IsWithdrawn && outcome.Order.DocTotal == 0m && !outcome.IsInvoiced);
        if (unpriced > 0)
        {
            found.Add(new(
                "low",
                "Orders without prices",
                $"{Count(unpriced, "order")} carr{(unpriced == 1 ? "ies" : "y")} no value",
                "Captured without prices, so they add nothing to the ordered value until a van invoices them. Usually a handset with a stale price list.",
                null, null, "orders"));
        }

        return found
            .OrderBy(item => item.Severity switch { "high" => 0, "medium" => 1, _ => 2 })
            .ThenByDescending(item => item.ValueAtStake ?? 0m)
            .ToList();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static bool Same(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static double Share(decimal part, decimal whole) => whole > 0 ? (double)(part / whole) : 0d;

    private static string Count(int count, string noun) => $"{count:N0} {noun}{(count == 1 ? "" : "s")}";

    private static decimal Round(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);
}
