using ShopInventory.Features.RouteCustomers.Queries;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.VanSalesReports.Queries;

/// <summary>
/// The measures every van sales report counts the same way.
///
/// These began as private statics on the performance report. They are here because the coverage
/// report needs them identically, and a second definition of "a productive call" or "a drop" would
/// let two pages disagree about a rep's strike rate with no way for a reader to tell which was
/// right — the same failure the fact reader exists to prevent one layer down.
///
/// Static and context-free, like <see cref="VanSalesFactReader"/>: nothing here holds state and
/// nothing needs registering.
/// </summary>
public static class VanSalesMeasures
{
    /// <summary>Stands in for "no shop was recorded" wherever a customer has to be a dictionary key.</summary>
    public const string Unattributed = "«unattributed»";

    /// <summary>A rep's name as reports render it.</summary>
    public sealed record UserName(string Username, string? FullName);

    /// <summary>
    /// The calls that bought: distinct shops per rep-day, with every unattributed sale on a day
    /// collapsing into one call between them.
    /// </summary>
    /// <remarks>
    /// This is the compliance report's definition, and every report has to keep it. Two things it
    /// must not become:
    ///
    /// Not an intersection of visits with sales. A van whose customers are real SAP business
    /// partners records no route customer on the sale while the visit still carries a code, so the
    /// intersection would zero those vans out entirely.
    ///
    /// Not one call per unattributed sale. They are the same unattributed bucket, and counting each
    /// separately makes the day a handset stopped reporting shops look like the busiest on the route.
    /// </remarks>
    public static int CountProductiveCalls(IEnumerable<VanSaleFact> facts) =>
        facts
            .GroupBy(fact => fact.Key)
            .Sum(day =>
                day.Where(fact => fact.RouteCustomerCode is not null)
                    .Select(fact => fact.RouteCustomerCode!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count()
                + (day.Any(fact => fact.RouteCustomerCode is null) ? 1 : 0));

    /// <summary>
    /// Distinct shops that bought. Keyed on the account as well as the code, because a route
    /// customer's code is unique only within its van's business partner.
    /// </summary>
    public static int CountOutletsThatBought(IEnumerable<VanSaleFact> facts) =>
        facts
            .Select(fact => fact.Outlet)
            .Where(outlet => outlet.HasValue)
            .Select(outlet => outlet!.Value)
            .Distinct()
            .Count();

    /// <summary>
    /// Calls made across a set of rep-days, or null when not one of those days has a visit record.
    ///
    /// Null rather than zero, because a rep with sales and no visit rows is not a rep who made no
    /// calls — he is one whose calls were never recorded, and a 0% strike rate would be a slander.
    /// </summary>
    public static int? CountCalls(
        IEnumerable<VanSalesDayKey> dayKeys,
        IReadOnlyDictionary<VanSalesDayKey, HashSet<string>> visits)
    {
        var known = dayKeys.Where(visits.ContainsKey).ToList();

        return known.Count == 0 ? null : known.Sum(key => visits[key].Count);
    }

    /// <summary>
    /// Distinct shops called on across the period — a different measure from calls, because a shop
    /// visited on Monday and Thursday is two calls and one outlet.
    /// </summary>
    public static int? CountOutletsVisited(
        IEnumerable<VanSalesDayKey> dayKeys,
        IReadOnlyDictionary<VanSalesDayKey, HashSet<string>> visits)
    {
        var keys = dayKeys.Where(visits.ContainsKey).ToList();

        if (keys.Count == 0)
        {
            return null;
        }

        return keys
            .SelectMany(key => visits[key])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
    }

    /// <summary>
    /// Distance covered, summing only the days that recorded both readings. Null when none did — an
    /// odometer that was never read is not a van that never moved, and a closing reading below the
    /// starting one is a mis-key rather than a reversal.
    /// </summary>
    public static int? SumKilometres(IEnumerable<VanRouteDayEntity> days)
    {
        var distances = days
            .Select(day => day.StartingMileage is { } start
                           && day.ClosingMileage is { } close
                           && close >= start
                ? close - start
                : (int?)null)
            .Where(distance => distance.HasValue)
            .Select(distance => distance!.Value)
            .ToList();

        return distances.Count > 0 ? distances.Sum() : null;
    }

    /// <summary>
    /// A drop is one shop, on one day, in one currency. Two invoices written at the same counter are
    /// one drop, which is what a field manager means by the word.
    /// </summary>
    public static (Guid UserId, DateTime TradingDate, string Customer) DropKey(VanSaleFact sale) =>
        (sale.UserId, sale.TradingDate, sale.RouteCustomerCode?.ToUpperInvariant() ?? Unattributed);

    public static List<VanSalesMoneyResult> MoneyByCurrency(IEnumerable<VanSaleFact> facts) =>
        facts
            .GroupBy(fact => RouteCustomerSalesReporting.NormalizeCurrency(fact.Currency),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => new VanSalesMoneyResult(
                Currency: group.Key,
                DocumentCount: group.Count(),
                DropCount: group.Select(DropKey).Distinct().Count(),
                Gross: group.Sum(fact => fact.TotalAmount)))
            .OrderByDescending(total => total.Gross)
            .ThenBy(total => total.Currency, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Rolls already-grouped currency rows up a level without ever crossing currencies.</summary>
    public static List<VanSalesMoneyResult> FoldMoney(IEnumerable<VanSalesMoneyResult> parts) =>
        parts
            .GroupBy(part => part.Currency, StringComparer.OrdinalIgnoreCase)
            .Select(group => new VanSalesMoneyResult(
                Currency: group.Key,
                DocumentCount: group.Sum(part => part.DocumentCount),
                DropCount: group.Sum(part => part.DropCount),
                Gross: group.Sum(part => part.Gross)))
            .OrderByDescending(total => total.Gross)
            .ThenBy(total => total.Currency, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static List<VanSalesLineMoneyResult> LineMoneyByCurrency(IEnumerable<VanSaleLineFact> lines) =>
        lines
            .GroupBy(line => RouteCustomerSalesReporting.NormalizeCurrency(line.Currency),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => new VanSalesLineMoneyResult(
                Currency: group.Key,
                LineCount: group.Count(),
                Gross: group.Sum(line => line.LineTotal)))
            .OrderByDescending(total => total.Gross)
            .ThenBy(total => total.Currency, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Quantity per unit of measure, never summed across them. Van lines carry no unit today —
    /// neither ingest path sets one — so this is usually a single "not recorded" bucket, and that is
    /// honest rather than broken.
    /// </summary>
    public static List<VanSalesQuantityResult> QuantitiesByUoM(IEnumerable<VanSaleLineFact> lines) =>
        lines
            .GroupBy(line => line.UoMCode)
            .Select(group => new VanSalesQuantityResult(
                UoMCode: group.Key,
                Quantity: group.Sum(line => line.Quantity),
                LineCount: group.Count()))
            .OrderByDescending(quantity => quantity.LineCount)
            .ThenBy(quantity => quantity.UoMCode, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Nearest-rank percentile over an ascending list. The list must not be empty.</summary>
    public static decimal Percentile(List<decimal> ascending, double fraction)
    {
        var rank = (int)Math.Ceiling(fraction * ascending.Count) - 1;
        return ascending[Math.Clamp(rank, 0, ascending.Count - 1)];
    }

    public static string? FirstDescription(IEnumerable<VanSaleLineFact> lines) =>
        lines
            .Select(line => line.ItemDescription)
            .FirstOrDefault(description => !string.IsNullOrWhiteSpace(description));

    /// <summary>
    /// The route a rep-day was worked on, taken from the day's own snapshot.
    ///
    /// Never from <c>RouteEntity</c> as it stands today and never from <c>User.RouteId</c>: the
    /// latter is the rep's current assignment, and using it would silently re-label every historical
    /// sale the moment a rep moves route. The snapshot exists precisely to stop that.
    /// </summary>
    public static (string? RouteCode, string? RouteName, string? Territory, bool HasRouteDay) RouteKeyOf(
        VanSalesDayKey key,
        IReadOnlyDictionary<VanSalesDayKey, VanRouteDayEntity> days) =>
        days.TryGetValue(key, out var day)
            ? (day.RouteCode, day.RouteName, day.Territory, true)
            : (null, null, null, false);
}
