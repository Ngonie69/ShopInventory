namespace ShopInventory.Web.Common;

/// <summary>
/// One delivery route from the routes workbook: the shops it serves, the day
/// (or days) it runs, and the truck sizes allocated to it.
/// </summary>
public sealed record DeliveryRoute(
    string Name,
    IReadOnlyList<string> Days,
    IReadOnlyList<string> Trucks,
    IReadOnlyList<string> CardCodes);

/// <summary>
/// The delivery routes, keyed by the SAP business partner codes each one calls
/// on. The table itself is generated into DeliveryRoutes.g.cs from the routes
/// workbook -- see scripts/DeliveryRoutes/generate_delivery_routes.py -- because
/// the workbook's own code column cannot be used as it stands: 28 of the codes
/// it carries name no partner in SAP, and one names a shop in the wrong
/// province. The generator resolves every stop against the partner master.
///
/// A partner sits on more than one route often enough that this is a
/// membership test rather than a label. Two reasons: a shop can genuinely be
/// called on twice a week (AMP Meats is on both PNP NORTH and CBD2), and the
/// Cheeseman depots all invoice to one partner, so that partner belongs to
/// every route that reprovisions a depot.
///
/// A code carries its currency as a suffix ("SPA059 USD", "CHE005 (FCA)"), and
/// the same shop holds a separate code per currency. All of them are listed
/// against the route, so filtering by route does not quietly drop a shop's
/// USD invoices because its ZiG code was the one in the workbook.
/// </summary>
public static partial class DeliveryRoutes
{
    // Lazy, not a plain initializer: RouteTable is declared in the generated
    // half of this class, and static fields initialize in file order, so a
    // direct initializer here reads it before it has been assigned.
    private static readonly Lazy<Dictionary<string, List<string>>> CardCodeIndex =
        new(BuildCardCodeIndex);

    private static readonly Lazy<string[]> RouteNames =
        new(() => RouteTable.Select(route => route.Name).ToArray());

    private static readonly Lazy<Dictionary<string, string>> CardNames =
        new(() => CardNameTable.ToDictionary(
            entry => NormalizeCardCode(entry.Code),
            entry => entry.Name,
            StringComparer.OrdinalIgnoreCase));

    public static IReadOnlyList<DeliveryRoute> All => RouteTable;

    /// <summary>Route names in the order the filter should offer them.</summary>
    public static IReadOnlyList<string> Names => RouteNames.Value;

    private static Dictionary<string, List<string>> BuildCardCodeIndex()
    {
        var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var route in RouteTable)
        {
            foreach (var cardCode in route.CardCodes)
            {
                var key = NormalizeCardCode(cardCode);
                if (key.Length == 0)
                {
                    continue;
                }

                if (!index.TryGetValue(key, out var routes))
                {
                    index[key] = routes = [];
                }

                if (!routes.Contains(route.Name, StringComparer.OrdinalIgnoreCase))
                {
                    routes.Add(route.Name);
                }
            }
        }

        return index;
    }

    /// <summary>
    /// Codes arrive from SAP exactly as the partner master holds them, but a
    /// code with a currency suffix is a space away from a code without one, so
    /// the whitespace is collapsed rather than trusted.
    /// </summary>
    public static string NormalizeCardCode(string? cardCode) =>
        string.IsNullOrWhiteSpace(cardCode)
            ? string.Empty
            : string.Join(' ', cardCode.Split(' ', StringSplitOptions.RemoveEmptyEntries |
                                                   StringSplitOptions.TrimEntries));

    /// <summary>
    /// The SAP name for a code the catalogue carries, or null for one it does
    /// not. Names come from the generated table rather than the partner cache,
    /// so a route still reads properly when a shop has been archived.
    /// </summary>
    public static string? GetCardName(string? cardCode)
    {
        var key = NormalizeCardCode(cardCode);
        return key.Length > 0 && CardNames.Value.TryGetValue(key, out var name) ? name : null;
    }

    /// <summary>
    /// The routes this partner is called on, or an empty list when the workbook
    /// does not place it on one.
    ///
    /// An empty result is ordinary, and common: measured over the POD report's
    /// default 30-day window on 2026-08-20, 32% of POD-eligible invoices were on
    /// a partner the workbook never lists. Most of that is food-service and trade
    /// accounts the trucks do not serve as route stops — restaurant groups, ice
    /// cream distributors, equipment suppliers — with Bulawayo-region shops only
    /// about a fifth of it, and a residue of retail shops opened since the
    /// workbook was drawn up. Do not read an empty result as a mapping fault.
    /// </summary>
    public static IReadOnlyList<string> GetRoutes(string? cardCode)
    {
        var key = NormalizeCardCode(cardCode);
        return key.Length > 0 && CardCodeIndex.Value.TryGetValue(key, out var routes)
            ? routes
            : [];
    }

    public static bool IsOnRoute(string? cardCode, string? routeName)
    {
        if (string.IsNullOrWhiteSpace(routeName))
        {
            return false;
        }

        var routes = GetRoutes(cardCode);
        for (var index = 0; index < routes.Count; index++)
        {
            if (string.Equals(routes[index], routeName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The partner's routes as one cell of text, for a table or an export.</summary>
    public static string FormatRoutes(string? cardCode) => string.Join(", ", GetRoutes(cardCode));

    /// <summary>
    /// A route with the day it runs, so the menu reads "BORROWDALE (Tue)" rather
    /// than leaving the reader to remember the schedule.
    /// </summary>
    public static string GetLabel(string? routeName)
    {
        if (string.IsNullOrWhiteSpace(routeName))
        {
            return string.Empty;
        }

        var route = RouteTable.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, routeName, StringComparison.OrdinalIgnoreCase));

        if (route is null || route.Days.Count == 0)
        {
            return routeName.Trim();
        }

        var days = string.Join('/', route.Days.Select(day =>
            day.Length > 3 ? day[..3] : day));

        return $"{route.Name} ({days})";
    }
}
