using System.Text.RegularExpressions;
using ShopInventory.Web.Common;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

/// <summary>One shop on a route, with the orders that came in from it.</summary>
/// <param name="Name">The shop as people say it, with the currency taken off its SAP name.</param>
/// <param name="CardCodes">Every code the shop is routed under — one per currency it trades in.</param>
/// <param name="IsOnHold">Every one of those codes is frozen in SAP, which is a credit hold.</param>
/// <param name="Orders">The orders that came in on any of those codes, earliest first.</param>
public sealed record RouteChecklistShop(
    string Name,
    IReadOnlyList<string> CardCodes,
    bool IsOnHold,
    IReadOnlyList<SalesOrderDto> Orders)
{
    public bool HasOrdered => Orders.Count > 0;
}

/// <summary>One delivery route and its shops, in the order the routes page lists them.</summary>
/// <param name="Route">The route as the Delivery Routes page has it.</param>
/// <param name="Shops">Every shop on the route, ordered or not, by name.</param>
/// <param name="RunDate">
/// The run of the route the checklist shows, when orders are matched to runs and the route has a day.
/// </param>
public sealed record RouteChecklistRoute(
    RouteSummary Route,
    IReadOnlyList<RouteChecklistShop> Shops,
    DateTime? RunDate = null)
{
    public int OrderedCount => Shops.Count(shop => shop.HasOrdered);
    public int OrderCount => Shops.Sum(shop => shop.Orders.Count);
}

/// <summary>
/// The checklist for a set of orders: each route with its shops ticked or not, and the orders whose
/// shop is on no route.
/// </summary>
public sealed record RouteChecklist(
    IReadOnlyList<RouteChecklistRoute> Routes,
    IReadOnlyList<SalesOrderDto> OffRouteOrders,
    int SkippedOrderCount)
{
    /// <summary>The orders on the checklist, each counted once however many routes its shop is on.</summary>
    public int OrderCount => Routes
        .SelectMany(route => route.Shops)
        .SelectMany(shop => shop.Orders)
        .Concat(OffRouteOrders)
        .Distinct(ReferenceEqualityComparer.Instance)
        .Count();
}

/// <summary>Which orders tick a shop on the checklist for a day.</summary>
public enum RouteChecklistMatch
{
    /// <summary>The orders for each route's run on or after the day. See <see cref="RouteOrderChecklist.BuildForRuns"/>.</summary>
    Run,

    /// <summary>The orders placed that day, in Harare time.</summary>
    OrderDate,

    /// <summary>The orders whose delivery date is that day.</summary>
    DeliveryDate
}

/// <summary>
/// Matches retail sales orders to the shops on each delivery route, so a reader can see which shops
/// on a round have ordered and which have not.
/// </summary>
/// <remarks>
/// <para>
/// The routes are the Delivery Routes page's map (<see cref="IDeliveryRouteDirectory"/>), so a shop
/// moved there moves here too. That map lists a shop once per currency code — Spar Bridge is
/// SPA002, SPA050 USD and SPA070 on Borrowdale — and a shop that ordered in USD has ordered, whatever
/// its ZiG code did. So codes are grouped into one shop by their SAP name with the currency words
/// taken off, and the shop is ticked when any of its codes has an order.
/// </para>
/// <para>
/// Codes held in Zimbabwe dollars are left out, as the routes page hides them: ZiG replaced ZWL in
/// April 2024 and each one duplicates a shop still served under another code. One that somehow has
/// an order is kept, so no order is ever dropped from the checklist.
/// </para>
/// <para>
/// A cancelled or rejected order is not an order placed, so it ticks nothing and is counted in
/// <see cref="RouteChecklist.SkippedOrderCount"/> instead.
/// </para>
/// </remarks>
public static partial class RouteOrderChecklist
{
    private static readonly HashSet<SalesOrderStatus> NotPlaced =
    [
        SalesOrderStatus.Cancelled,
        SalesOrderStatus.Rejected
    ];

    /// <summary>Every order given ticks its shop: the caller has already chosen the day's orders.</summary>
    public static RouteChecklist Build(
        RouteMap map,
        IEnumerable<SalesOrderDto> orders,
        IReadOnlyDictionary<string, ShopStanding>? standings = null) =>
        Build(map, orders, standings, _ => new RouteWindow(null, _ => true), _ => true);

    /// <summary>
    /// The checklist for each route's run on or after <paramref name="day"/>: a shop is ticked by an
    /// order meant for that run, whichever day before it the order was placed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reps order for a route's run in the days before it. Borrowdale runs on Tuesdays and its shops
    /// order on the Monday, so matching on the day an order was placed showed the route as unordered
    /// on the day its truck went out full. Each order instead belongs to one run of each route its
    /// shop is on: the first run on or after the Harare day it was placed (see <see cref="RunOf"/>).
    /// </para>
    /// <para>
    /// A route with no day set has no runs, so its shops are ticked by orders placed on
    /// <paramref name="day"/>, as are the orders from shops on no route.
    /// </para>
    /// <para>
    /// <paramref name="orders"/> must hold every order placed from a week before <paramref name="day"/>
    /// to a week after it: a run can be six days off, and its orders placed up to six days before it.
    /// </para>
    /// </remarks>
    public static RouteChecklist BuildForRuns(
        RouteMap map,
        IEnumerable<SalesOrderDto> orders,
        DateTime day,
        IReadOnlyDictionary<string, ShopStanding>? standings = null)
    {
        day = day.Date;
        return Build(map, orders, standings, WindowFor, PlacedOnDay);

        RouteWindow WindowFor(RouteSummary route)
        {
            var runDays = RunDays(route);
            if (runDays.Count == 0)
            {
                return new RouteWindow(null, PlacedOnDay);
            }

            var run = NextRun(day, runDays);
            return new RouteWindow(run, order => RunOf(order, runDays) == run);
        }

        bool PlacedOnDay(SalesOrderDto order) => HarareDay(order.OrderDate) == day;
    }

    /// <summary>The weekdays a route runs on, read from the routes page's day names.</summary>
    internal static IReadOnlySet<DayOfWeek> RunDays(RouteSummary route)
    {
        var days = new HashSet<DayOfWeek>();
        foreach (var name in route.Days)
        {
            var trimmed = name.Trim();
            if (trimmed.Length < 3)
            {
                continue;
            }

            foreach (var weekDay in Enum.GetValues<DayOfWeek>())
            {
                if (weekDay.ToString().StartsWith(trimmed[..3], StringComparison.OrdinalIgnoreCase))
                {
                    days.Add(weekDay);
                }
            }
        }

        return days;
    }

    /// <summary>
    /// The run of a route an order is for: the first run on or after the Harare day it was placed.
    /// </summary>
    /// <remarks>
    /// An order placed on a run day counts for that day's run. On Tuesday 27 Oct 2026 the Tuesday
    /// routes' same-day orders were invoiced that day, Marondera's among them, although the handset
    /// had given every one a delivery date of the next Tuesday — so the delivery date cannot be used
    /// to move them to the following run. The cost is that an order taken after the truck has left
    /// counts for the run it missed.
    /// </remarks>
    internal static DateTime RunOf(SalesOrderDto order, IReadOnlySet<DayOfWeek> runDays) =>
        NextRun(HarareDay(order.OrderDate), runDays);

    /// <summary>The first day on or after <paramref name="from"/> that the route runs.</summary>
    internal static DateTime NextRun(DateTime from, IReadOnlySet<DayOfWeek> runDays)
    {
        var date = from.Date;
        for (var i = 0; i < 7 && !runDays.Contains(date.DayOfWeek); i++)
        {
            date = date.AddDays(1);
        }

        return date;
    }

    /// <summary>
    /// The Harare day (UTC+2, no daylight saving) of an instant the API sends in UTC. An order placed
    /// at 00:30 in Harare is 22:30 UTC the day before.
    /// </summary>
    internal static DateTime HarareDay(DateTime instant)
    {
        var utc = instant.Kind == DateTimeKind.Local ? instant.ToUniversalTime() : instant;
        return IAuditService.ToCAT(utc).Date;
    }

    /// <summary>The run a route shows and which orders belong to it.</summary>
    private readonly record struct RouteWindow(DateTime? RunDate, Func<SalesOrderDto, bool> Includes);

    private static RouteChecklist Build(
        RouteMap map,
        IEnumerable<SalesOrderDto> orders,
        IReadOnlyDictionary<string, ShopStanding>? standings,
        Func<RouteSummary, RouteWindow> windowFor,
        Func<SalesOrderDto, bool> offRouteIncludes)
    {
        standings ??= new Dictionary<string, ShopStanding>();

        var ordersByCode = new Dictionary<string, List<SalesOrderDto>>(StringComparer.OrdinalIgnoreCase);
        foreach (var order in orders)
        {
            var code = DeliveryRoutes.NormalizeCardCode(order.CardCode);
            if (!ordersByCode.TryGetValue(code, out var list))
            {
                ordersByCode[code] = list = [];
            }

            list.Add(order);
        }

        // A cancelled order is counted once, however many of its shop's routes it falls in.
        var skipped = new HashSet<SalesOrderDto>(ReferenceEqualityComparer.Instance);
        var routedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var routes = new List<RouteChecklistRoute>(map.AllRoutes.Count);
        foreach (var route in map.AllRoutes)
        {
            var window = windowFor(route);
            var placedByCode = PlacedOrders(ordersByCode, window.Includes, skipped);
            var shops = BuildShops(map.GetStops(route.Name), placedByCode, standings, routedCodes);
            routes.Add(new RouteChecklistRoute(route, shops, window.RunDate));
        }

        var offRouteCodes = ordersByCode
            .Where(entry => !routedCodes.Contains(entry.Key))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
        var offRoute = PlacedOrders(offRouteCodes, offRouteIncludes, skipped)
            .SelectMany(entry => entry.Value)
            .OrderBy(order => order.CardName ?? order.CardCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(order => order.OrderDate)
            .ToList();

        return new RouteChecklist(routes, offRoute, skipped.Count);
    }

    /// <summary>
    /// The placed orders in a window, by code. The cancelled and rejected ones in it go to
    /// <paramref name="skipped"/> instead.
    /// </summary>
    private static Dictionary<string, List<SalesOrderDto>> PlacedOrders(
        Dictionary<string, List<SalesOrderDto>> ordersByCode,
        Func<SalesOrderDto, bool> includes,
        HashSet<SalesOrderDto> skipped)
    {
        var placed = new Dictionary<string, List<SalesOrderDto>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (code, codeOrders) in ordersByCode)
        {
            foreach (var order in codeOrders)
            {
                if (!includes(order))
                {
                    continue;
                }

                if (NotPlaced.Contains(order.Status))
                {
                    skipped.Add(order);
                    continue;
                }

                if (!placed.TryGetValue(code, out var list))
                {
                    placed[code] = list = [];
                }

                list.Add(order);
            }
        }

        return placed;
    }

    private static List<RouteChecklistShop> BuildShops(
        IReadOnlyList<RouteStop> stops,
        Dictionary<string, List<SalesOrderDto>> ordersByCode,
        IReadOnlyDictionary<string, ShopStanding> standings,
        HashSet<string> routedCodes)
    {
        var groups = new Dictionary<string, List<RouteStop>>(StringComparer.Ordinal);
        var groupOrder = new List<string>();

        foreach (var stop in stops)
        {
            var code = DeliveryRoutes.NormalizeCardCode(stop.CardCode);
            if (code.Length == 0)
            {
                continue;
            }

            var hasOrders = ordersByCode.ContainsKey(code);
            var standing = standings.TryGetValue(code, out var found) ? found : null;
            if (standing is { IsRetiredCurrency: true } && !hasOrders)
            {
                continue;
            }

            // A stop whose name the map does not know keys on its code, so two unnamed codes are
            // never folded into one shop.
            var key = ShopKey(stop.CardName);
            if (key.Length == 0)
            {
                key = "code:" + code;
            }

            if (!groups.TryGetValue(key, out var members))
            {
                groups[key] = members = [];
                groupOrder.Add(key);
            }

            members.Add(stop);
        }

        var shops = new List<RouteChecklistShop>(groupOrder.Count);
        foreach (var key in groupOrder)
        {
            var members = groups[key];
            var codes = members
                .Select(member => member.CardCode.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var shopOrders = new List<SalesOrderDto>();
            var allFrozen = true;
            foreach (var code in codes)
            {
                var normalized = DeliveryRoutes.NormalizeCardCode(code);
                routedCodes.Add(normalized);
                if (ordersByCode.TryGetValue(normalized, out var codeOrders))
                {
                    shopOrders.AddRange(codeOrders);
                }

                if (!standings.TryGetValue(normalized, out var standing) || !standing.IsFrozen)
                {
                    allFrozen = false;
                }
            }

            shopOrders.Sort((a, b) => a.OrderDate.CompareTo(b.OrderDate));
            shops.Add(new RouteChecklistShop(DisplayName(members), codes, allFrozen, shopOrders));
        }

        shops.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return shops;
    }

    /// <summary>The shop's name with the currency words and punctuation taken off, lower-cased.</summary>
    internal static string ShopKey(string? cardName)
    {
        if (string.IsNullOrWhiteSpace(cardName))
        {
            return string.Empty;
        }

        var stripped = CurrencyWord().Replace(cardName, " ");
        stripped = NotAlphanumeric().Replace(stripped, " ");
        return Whitespace().Replace(stripped, " ").Trim().ToLowerInvariant();
    }

    private static string DisplayName(List<RouteStop> members)
    {
        var name = members
            .Select(member => member.CardName)
            .Where(cardName => !string.IsNullOrWhiteSpace(cardName))
            .OrderBy(cardName => cardName.Length)
            .FirstOrDefault();

        if (name is null)
        {
            return members[0].CardCode.Trim();
        }

        var cleaned = Whitespace().Replace(CurrencyWord().Replace(name, " "), " ").Trim();
        cleaned = cleaned.Replace("()", string.Empty).Trim().TrimEnd('-', ',', '/').Trim();
        return cleaned.Length > 0 ? cleaned : name.Trim();
    }

    [GeneratedRegex(@"\(?\b(USD|ZIG|ZWG|ZWL|RTGS|FCA)\b\)?", RegexOptions.IgnoreCase)]
    private static partial Regex CurrencyWord();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NotAlphanumeric();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
