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
public sealed record RouteChecklistRoute(RouteSummary Route, IReadOnlyList<RouteChecklistShop> Shops)
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
    int SkippedOrderCount);

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

    public static RouteChecklist Build(
        RouteMap map,
        IEnumerable<SalesOrderDto> orders,
        IReadOnlyDictionary<string, ShopStanding>? standings = null)
    {
        standings ??= new Dictionary<string, ShopStanding>();

        var ordersByCode = new Dictionary<string, List<SalesOrderDto>>(StringComparer.OrdinalIgnoreCase);
        var skipped = 0;
        foreach (var order in orders)
        {
            if (NotPlaced.Contains(order.Status))
            {
                skipped++;
                continue;
            }

            var code = DeliveryRoutes.NormalizeCardCode(order.CardCode);
            if (!ordersByCode.TryGetValue(code, out var list))
            {
                ordersByCode[code] = list = [];
            }

            list.Add(order);
        }

        var routedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var routes = new List<RouteChecklistRoute>(map.AllRoutes.Count);
        foreach (var route in map.AllRoutes)
        {
            var shops = BuildShops(map.GetStops(route.Name), ordersByCode, standings, routedCodes);
            routes.Add(new RouteChecklistRoute(route, shops));
        }

        var offRoute = ordersByCode
            .Where(entry => !routedCodes.Contains(entry.Key))
            .SelectMany(entry => entry.Value)
            .OrderBy(order => order.CardName ?? order.CardCode, StringComparer.OrdinalIgnoreCase)
            .ThenBy(order => order.OrderDate)
            .ToList();

        return new RouteChecklist(routes, offRoute, skipped);
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
