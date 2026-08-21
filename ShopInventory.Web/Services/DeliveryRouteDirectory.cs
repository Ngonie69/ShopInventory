using Microsoft.EntityFrameworkCore;
using ShopInventory.Web.Common;
using ShopInventory.Web.Data;

namespace ShopInventory.Web.Services;

/// <summary>One shop on one route, and whether the workbook put it there.</summary>
public sealed record RouteStop(string CardCode, string CardName, bool FromWorkbook);

/// <summary>A shop offered for adding to a route, and the routes it is on now.</summary>
public sealed record ShopCandidate(string CardCode, string CardName, IReadOnlyList<string> Routes);

/// <summary>A shop on no route at all, offered for placing on one.</summary>
public sealed record UnplacedShop(string CardCode, string CardName);

/// <summary>
/// What SAP says about a shop, for the two things that decide whether a truck
/// should still be routed to its code.
/// </summary>
/// <param name="IsFrozen">
/// SAP's Frozen flag. Read as a credit hold, not a closed shop: measured
/// against live SAP on 2026-08-20, 49 route stops were frozen while still on a
/// currency in use, and several of those had been invoiced that quarter
/// (Megasave Mvurwi 2026-07-30, Sai Mart Gweru 2026-07-29). So this marks a row
/// and never hides one.
/// </param>
/// <param name="IsRetiredCurrency">
/// The code is held in Zimbabwe dollars, which stopped being used when ZiG
/// replaced ZWL in April 2024. A shop holds one code per currency, so a retired
/// code is a duplicate row of a shop that is still served under its USD or ZiG
/// code. 111 of the catalogue's 368 stops are these, and dropping every one of
/// them leaves no route without a code for a shop it actually calls at.
/// </param>
public sealed record ShopStanding(bool IsFrozen, bool IsRetiredCurrency);

/// <summary>
/// What the unassigned pane shows: a bounded page of shops, and how many there
/// are altogether. The two differ because the list is capped -- there are
/// several hundred shops on no route, and rendering them all would cost far
/// more than anyone reads.
/// </summary>
public sealed record UnplacedShops(IReadOnlyList<UnplacedShop> Shops, int Total)
{
    public static readonly UnplacedShops None = new([], 0);
}

/// <summary>
/// One staged reassignment, before it is written. The page collects these while
/// somebody rearranges routes and saves them in one go.
/// </summary>
public sealed record RouteChange(
    string CardCode, string? CardName, string RouteName, bool IsRemoval, string? Note = null);

/// <summary>
/// Everything held about routes outside the generated catalogue: the
/// reassignments and the routes somebody added. Read once, so that a page
/// rearranging routes can work out what its unsaved changes would come to
/// without going back to the database on every drag.
/// </summary>
public sealed record RouteState(
    IReadOnlyList<RouteAssignmentOverride> Overrides,
    IReadOnlyList<CustomDeliveryRoute> AddedRoutes);

/// <summary>
/// A route as the page needs to show it: what it is called, when it runs, how
/// many shops it has, and whether it came from the workbook or somebody added it.
/// </summary>
public sealed record RouteSummary(
    string Name,
    string Label,
    IReadOnlyList<string> Days,
    string? Truck,
    bool IsCustom,
    int StopCount);

/// <summary>
/// The routes as they currently stand: the generated catalogue, plus any routes
/// added since, with everybody's reassignments applied. Immutable, and every
/// lookup on it is synchronous, so a caller reads it once and then filters a few
/// thousand report rows against it without going back to the database per row.
/// </summary>
public sealed class RouteMap
{
    private readonly Dictionary<string, List<string>> _routesByCardCode;
    private readonly Dictionary<string, List<RouteStop>> _stopsByRoute;
    // (normalised card code, upper-cased route) for the shops somebody placed.
    // A tuple rather than a joined string: card codes contain spaces
    // ("SPA059 USD"), so any single-character separator can be ambiguous.
    private readonly HashSet<(string CardCode, string Route)> _reassigned;

    internal RouteMap(
        Dictionary<string, List<string>> routesByCardCode,
        Dictionary<string, List<RouteStop>> stopsByRoute,
        HashSet<(string, string)> reassigned,
        IReadOnlyList<RouteSummary> allRoutes,
        IReadOnlyList<string> namesWithStops)
    {
        _routesByCardCode = routesByCardCode;
        _stopsByRoute = stopsByRoute;
        _reassigned = reassigned;
        AllRoutes = allRoutes;
        Names = namesWithStops;
    }

    /// <summary>
    /// Every route, including ones with no shops on them. A route just added has
    /// none until shops are put on it, and one emptied by removals can be filled
    /// again, so both have to stay visible to the page that manages them.
    /// </summary>
    public IReadOnlyList<RouteSummary> AllRoutes { get; }

    /// <summary>
    /// Routes that actually have shops, for a filter. Offering a route that can
    /// select nothing is just a dead option.
    /// </summary>
    public IReadOnlyList<string> Names { get; }

    public IReadOnlyList<string> GetRoutes(string? cardCode)
    {
        var key = DeliveryRoutes.NormalizeCardCode(cardCode);
        return key.Length > 0 && _routesByCardCode.TryGetValue(key, out var routes) ? routes : [];
    }

    public bool IsOnRoute(string? cardCode, string? routeName)
    {
        if (string.IsNullOrWhiteSpace(routeName))
        {
            return false;
        }

        foreach (var route in GetRoutes(cardCode))
        {
            if (string.Equals(route, routeName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public string FormatRoutes(string? cardCode) => string.Join(", ", GetRoutes(cardCode));

    public IReadOnlyList<RouteStop> GetStops(string routeName) =>
        _stopsByRoute.TryGetValue(routeName, out var stops) ? stops : [];

    /// <summary>Whether a person put this shop on this route, rather than the workbook.</summary>
    public bool IsReassigned(string? cardCode, string routeName) =>
        _reassigned.Contains((
            DeliveryRoutes.NormalizeCardCode(cardCode),
            routeName.Trim().ToUpperInvariant()));

    public RouteSummary? GetRoute(string? routeName) =>
        routeName is null
            ? null
            : AllRoutes.FirstOrDefault(route =>
                string.Equals(route.Name, routeName, StringComparison.OrdinalIgnoreCase));

    /// <summary>The route with the day it runs, e.g. "BORROWDALE (Tue)".</summary>
    public string GetLabel(string? routeName) => GetRoute(routeName)?.Label ?? routeName?.Trim() ?? string.Empty;
}

public interface IDeliveryRouteDirectory
{
    Task<RouteMap> GetMapAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The saved reassignments and added routes, for a caller that needs to
    /// project unsaved changes on top of them.
    /// </summary>
    Task<RouteState> GetStateAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RouteAssignmentOverride>> GetOverridesAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Put a shop on a route, or cancel a removal that took it off one.</summary>
    Task AssignAsync(
        string cardCode, string? cardName, string routeName,
        string? note, string username, CancellationToken cancellationToken = default);

    /// <summary>Take a shop off a route, or cancel an assignment that put it on one.</summary>
    Task UnassignAsync(
        string cardCode, string? cardName, string routeName,
        string? note, string username, CancellationToken cancellationToken = default);

    /// <summary>Drop every override for this shop, putting it back where the workbook has it.</summary>
    Task<int> ResetAsync(string cardCode, string username, CancellationToken cancellationToken = default);

    /// <summary>
    /// Add a route the workbook does not define. Throws when the name is blank
    /// or already taken, by the workbook or by another added route.
    /// </summary>
    Task CreateRouteAsync(
        string name, IEnumerable<string>? days, string? truck,
        string? note, string username, CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove an added route, and with it every assignment onto that route.
    /// Workbook routes cannot be deleted — regenerating would bring them back.
    /// </summary>
    Task DeleteRouteAsync(string name, string username, CancellationToken cancellationToken = default);

    /// <summary>
    /// Customers matching the term, for adding to a route. Searches the whole
    /// partner cache rather than the routes: a shop that is on no route yet is
    /// exactly the one somebody is most likely to be looking for, and a route
    /// just created has nothing on it at all.
    /// </summary>
    Task<IReadOnlyList<ShopCandidate>> SearchShopsAsync(
        string term, RouteMap map, string? excludingRoute = null,
        int limit = 12, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every active customer, code and name only, for the pane that places
    /// shops on routes.
    /// </summary>
    /// <remarks>
    /// The whole set rather than a filtered page, and no route filtering here:
    /// which shops have a route changes with every unsaved move, so the caller
    /// works that out against its own map. Read once per load and filtered in
    /// memory afterwards -- a query per keystroke over a few thousand partners
    /// is the shape that makes a filter box feel broken.
    /// </remarks>
    Task<IReadOnlyList<UnplacedShop>> ListActiveShopsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// How each shop stands with SAP, for the codes where it is worth saying.
    /// Only codes that are frozen or on a retired currency appear; anything the
    /// cache does not mention is in good standing.
    /// </summary>
    /// <remarks>
    /// Keyed only on the ones worth flagging so that a code the cache does not
    /// hold at all reads as "nothing known against it" rather than as retired.
    /// That distinction is what stops a half-synced cache emptying every route
    /// on the page.
    /// </remarks>
    Task<IReadOnlyDictionary<string, ShopStanding>> GetShopStandingsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Write a batch of staged reassignments, all in one save. Returns how many
    /// of them actually changed anything -- one that the workbook already
    /// agrees with is dropped rather than stored.
    /// </summary>
    Task<int> ApplyAsync(
        IReadOnlyList<RouteChange> changes, string username,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Merges the generated catalogue with the routes and reassignments held in the
/// database.
///
/// Deliberately uncached. Both tables hold one row per human decision — tens,
/// not thousands — and a caller reads the map once per report or page render, so
/// a query costs nothing measurable. A cache would buy nothing and would go
/// stale across instances, which is exactly the failure that is hard to spot: a
/// shop moved on one instance and still on its old route on another.
/// </summary>
public sealed class DeliveryRouteDirectory(
    WebAppDbContext context,
    ILogger<DeliveryRouteDirectory> logger) : IDeliveryRouteDirectory
{
    public async Task<RouteMap> GetMapAsync(CancellationToken cancellationToken = default)
    {
        var overrides = await context.RouteAssignmentOverrides
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var custom = await context.CustomDeliveryRoutes
            .AsNoTracking()
            .OrderBy(route => route.Name)
            .ToListAsync(cancellationToken);

        return Build(overrides, custom);
    }

    public async Task<RouteState> GetStateAsync(CancellationToken cancellationToken = default) =>
        new(await GetOverridesAsync(cancellationToken),
            await context.CustomDeliveryRoutes
                .AsNoTracking()
                .OrderBy(route => route.Name)
                .ToListAsync(cancellationToken));

    /// <summary>
    /// The map as it would stand with these unsaved changes applied on top of
    /// the saved ones -- what the page draws while somebody rearranges routes.
    /// </summary>
    /// <remarks>
    /// Deliberately the same reasoning as <c>StageAsync</c>, applied to the
    /// same override list rather than to the database: a staged change replaces
    /// the saved row for its shop-and-route pair, and one the workbook already
    /// agrees with leaves no row at all. Written once here so that what the
    /// page shows before saving and what the database holds afterwards cannot
    /// come apart.
    /// </remarks>
    public static RouteMap Project(RouteState state, IEnumerable<RouteChange> staged)
    {
        var changes = staged.ToList();
        var replaced = changes.Select(KeyOf).ToHashSet();

        var effective = state.Overrides
            .Where(row => !replaced.Contains(KeyOf(row)))
            .ToList();

        foreach (var change in changes)
        {
            if (!IsMeaningfulOverride(change.CardCode, change.RouteName, change.IsRemoval))
            {
                continue;
            }

            effective.Add(new RouteAssignmentOverride
            {
                CardCode = DeliveryRoutes.NormalizeCardCode(change.CardCode),
                CardName = change.CardName,
                RouteName = change.RouteName.Trim(),
                IsRemoval = change.IsRemoval,
                Note = change.Note
            });
        }

        return Build(effective, state.AddedRoutes);
    }

    /// <summary>One shop on one route, however the code was spaced or cased.</summary>
    public static (string Code, string Route) KeyOf(string? cardCode, string? routeName) =>
        (DeliveryRoutes.NormalizeCardCode(cardCode).ToUpperInvariant(),
         (routeName ?? string.Empty).Trim().ToUpperInvariant());

    private static (string, string) KeyOf(RouteAssignmentOverride row) => KeyOf(row.CardCode, row.RouteName);

    private static (string, string) KeyOf(RouteChange change) => KeyOf(change.CardCode, change.RouteName);

    internal static IReadOnlyList<string> SplitDays(string? days) =>
        string.IsNullOrWhiteSpace(days)
            ? []
            : days.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string BuildLabel(string name, IReadOnlyList<string> days)
    {
        if (days.Count == 0)
        {
            return name;
        }

        return $"{name} ({string.Join('/', days.Select(day => day.Length > 3 ? day[..3] : day))})";
    }

    internal static RouteMap Build(
        IReadOnlyList<RouteAssignmentOverride> overrides,
        IReadOnlyList<CustomDeliveryRoute>? customRoutes = null)
    {
        // Every route in play, workbook first so an added route can never shadow
        // one the workbook defines.
        var order = new List<(string Name, IReadOnlyList<string> Days, string? Truck, bool IsCustom)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var route in DeliveryRoutes.All)
        {
            order.Add((route.Name, route.Days, route.Trucks.FirstOrDefault(), false));
            seen.Add(route.Name);
        }

        foreach (var route in customRoutes ?? [])
        {
            var name = (route.Name ?? string.Empty).Trim();
            if (name.Length == 0 || !seen.Add(name))
            {
                continue;
            }

            order.Add((name, SplitDays(route.Days), route.Truck, true));
        }

        // route -> code -> stop, seeded from the workbook.
        var byRoute = new Dictionary<string, Dictionary<string, RouteStop>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, _, _, _) in order)
        {
            byRoute[name] = new Dictionary<string, RouteStop>(StringComparer.OrdinalIgnoreCase);
        }

        foreach (var route in DeliveryRoutes.All)
        {
            var stops = byRoute[route.Name];
            foreach (var cardCode in route.CardCodes)
            {
                var key = DeliveryRoutes.NormalizeCardCode(cardCode);
                stops[key] = new RouteStop(cardCode, DeliveryRoutes.GetCardName(cardCode) ?? cardCode, true);
            }
        }

        var reassigned = new HashSet<(string, string)>();
        foreach (var row in overrides)
        {
            // A row naming a route that no longer exists is stale, not fatal:
            // the workbook gets regenerated and an added route can be deleted.
            if (!byRoute.TryGetValue(row.RouteName, out var stops))
            {
                continue;
            }

            var key = DeliveryRoutes.NormalizeCardCode(row.CardCode);
            if (key.Length == 0)
            {
                continue;
            }

            if (row.IsRemoval)
            {
                stops.Remove(key);
            }
            else
            {
                stops[key] = new RouteStop(
                    row.CardCode.Trim(),
                    row.CardName ?? DeliveryRoutes.GetCardName(row.CardCode) ?? row.CardCode.Trim(),
                    false);
                reassigned.Add((key, row.RouteName.Trim().ToUpperInvariant()));
            }
        }

        var routesByCardCode = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var stopsByRoute = new Dictionary<string, List<RouteStop>>(StringComparer.OrdinalIgnoreCase);
        var all = new List<RouteSummary>();
        var withStops = new List<string>();

        foreach (var (name, days, truck, isCustom) in order)
        {
            var stops = byRoute[name];
            all.Add(new RouteSummary(name, BuildLabel(name, days), days, truck, isCustom, stops.Count));

            stopsByRoute[name] = stops.Values
                .OrderBy(stop => stop.CardName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (stops.Count == 0)
            {
                continue;
            }

            withStops.Add(name);
            foreach (var key in stops.Keys)
            {
                if (!routesByCardCode.TryGetValue(key, out var list))
                {
                    routesByCardCode[key] = list = [];
                }

                list.Add(name);
            }
        }

        return new RouteMap(routesByCardCode, stopsByRoute, reassigned, all, withStops);
    }

    public async Task<IReadOnlyList<RouteAssignmentOverride>> GetOverridesAsync(
        CancellationToken cancellationToken = default) =>
        await context.RouteAssignmentOverrides
            .AsNoTracking()
            .OrderByDescending(row => row.CreatedAtUtc)
            .ThenByDescending(row => row.Id)
            .ToListAsync(cancellationToken);

    public Task AssignAsync(
        string cardCode, string? cardName, string routeName,
        string? note, string username, CancellationToken cancellationToken = default) =>
        WriteAsync(cardCode, cardName, routeName, isRemoval: false, note, username, cancellationToken);

    public Task UnassignAsync(
        string cardCode, string? cardName, string routeName,
        string? note, string username, CancellationToken cancellationToken = default) =>
        WriteAsync(cardCode, cardName, routeName, isRemoval: true, note, username, cancellationToken);

    /// <summary>
    /// Whether an override would actually say anything, or whether the workbook
    /// already agrees with it.
    /// </summary>
    /// <remarks>
    /// Storing one the workbook agrees with would leave a row that says nothing
    /// and would show the shop as reassigned when it was never moved. The page
    /// asks this before staging a change so that dragging a shop back where it
    /// started leaves no trace, rather than leaving a change to save that would
    /// then be silently discarded.
    /// </remarks>
    public static bool IsMeaningfulOverride(string? cardCode, string? routeName, bool isRemoval)
    {
        var code = DeliveryRoutes.NormalizeCardCode(cardCode);
        if (code.Length == 0 || string.IsNullOrWhiteSpace(routeName))
        {
            return false;
        }

        // A removal says something only where the workbook puts the shop here;
        // an assignment only where it does not.
        return isRemoval == DeliveryRoutes.IsOnRoute(code, routeName);
    }

    private async Task WriteAsync(
        string cardCode, string? cardName, string routeName, bool isRemoval,
        string? note, string username, CancellationToken cancellationToken)
    {
        if (await StageAsync(cardCode, cardName, routeName, isRemoval, note, username, cancellationToken))
        {
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Works out what one reassignment means for the override table and puts it
    /// on the context, without saving. Returns whether anything was put there.
    /// </summary>
    /// <remarks>
    /// Split from the save so that one change and a batch of forty go through
    /// exactly the same rules -- the batch is the same call in a loop with one
    /// SaveChanges after it, rather than a second copy of this reasoning.
    /// </remarks>
    private async Task<bool> StageAsync(
        string cardCode, string? cardName, string routeName, bool isRemoval,
        string? note, string username, CancellationToken cancellationToken)
    {
        var code = DeliveryRoutes.NormalizeCardCode(cardCode);
        if (code.Length == 0 || string.IsNullOrWhiteSpace(routeName))
        {
            throw new ArgumentException("A reassignment needs both a card code and a route");
        }

        routeName = routeName.Trim();

        var existing = await context.RouteAssignmentOverrides
            .FirstOrDefaultAsync(row => row.CardCode == code && row.RouteName == routeName, cancellationToken);

        if (!IsMeaningfulOverride(code, routeName, isRemoval))
        {
            // Removing a shop the workbook never placed here, or adding one it
            // already places here: the request is a no-op against the workbook,
            // so the right state is no override at all.
            if (existing is null)
            {
                return false;
            }

            context.RouteAssignmentOverrides.Remove(existing);
            logger.LogInformation(
                "{User} cleared the route override for {CardCode} on {Route}; the workbook already agrees",
                username, code, routeName);
            return true;
        }

        if (existing is not null)
        {
            existing.IsRemoval = isRemoval;
            existing.CardName = cardName ?? existing.CardName;
            existing.Note = note;
            existing.CreatedAtUtc = DateTime.UtcNow;
            existing.CreatedBy = username;
        }
        else
        {
            context.RouteAssignmentOverrides.Add(new RouteAssignmentOverride
            {
                CardCode = code,
                CardName = cardName,
                RouteName = routeName,
                IsRemoval = isRemoval,
                Note = note,
                CreatedAtUtc = DateTime.UtcNow,
                CreatedBy = username
            });
        }

        logger.LogInformation(
            "{User} {Verb} {CardCode} {Preposition} route {Route}",
            username, isRemoval ? "removed" : "assigned", code, isRemoval ? "from" : "to", routeName);
        return true;
    }

    public async Task<int> ApplyAsync(
        IReadOnlyList<RouteChange> changes, string username,
        CancellationToken cancellationToken = default)
    {
        // One change per shop-and-route wins, the last staged. Two rows for the
        // same pair would each read the table before either was saved, so the
        // second would not see the first and the write order would decide the
        // outcome. The page keys its staged changes the same way.
        var deduped = changes
            .GroupBy(KeyOf)
            .Select(group => group.Last())
            .ToList();

        var written = 0;
        foreach (var change in deduped)
        {
            if (await StageAsync(
                    change.CardCode, change.CardName, change.RouteName,
                    change.IsRemoval, change.Note, username, cancellationToken))
            {
                written++;
            }
        }

        if (written > 0)
        {
            await context.SaveChangesAsync(cancellationToken);
        }

        logger.LogInformation(
            "{User} saved {Written} route change(s) of {Staged} staged", username, written, deduped.Count);
        return written;
    }

    public async Task<int> ResetAsync(
        string cardCode, string username, CancellationToken cancellationToken = default)
    {
        var code = DeliveryRoutes.NormalizeCardCode(cardCode);
        var rows = await context.RouteAssignmentOverrides
            .Where(row => row.CardCode == code)
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return 0;
        }

        context.RouteAssignmentOverrides.RemoveRange(rows);
        await context.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "{User} reset {CardCode} to the routes the workbook gives it, dropping {Count} override(s)",
            username, code, rows.Count);
        return rows.Count;
    }

    public async Task CreateRouteAsync(
        string name, IEnumerable<string>? days, string? truck,
        string? note, string username, CancellationToken cancellationToken = default)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            throw new ArgumentException("A route needs a name");
        }

        if (DeliveryRoutes.Names.Any(existing =>
                string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"\"{name}\" is already a route in the routes workbook");
        }

        var taken = await context.CustomDeliveryRoutes
            .AnyAsync(route => route.Name.ToLower() == name.ToLower(), cancellationToken);
        if (taken)
        {
            throw new InvalidOperationException($"\"{name}\" is already a route");
        }

        context.CustomDeliveryRoutes.Add(new CustomDeliveryRoute
        {
            Name = name,
            Days = days is null ? null : string.Join(',', days.Where(day => !string.IsNullOrWhiteSpace(day))),
            Truck = string.IsNullOrWhiteSpace(truck) ? null : truck.Trim(),
            Note = note,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = username
        });

        await context.SaveChangesAsync(cancellationToken);
        logger.LogInformation("{User} added delivery route {Route}", username, name);
    }

    public async Task<IReadOnlyList<ShopCandidate>> SearchShopsAsync(
        string term, RouteMap map, string? excludingRoute = null,
        int limit = 12, CancellationToken cancellationToken = default)
    {
        term = (term ?? string.Empty).Trim();
        if (term.Length < 2)
        {
            return [];
        }

        // Active customers only, matching how the rest of the Web reads this
        // cache. A frozen or archived partner is not somewhere a truck calls.
        var rows = await context.CachedBusinessPartners
            .AsNoTracking()
            .Where(partner => partner.IsActive
                && partner.CardType == "cCustomer"
                && (EF.Functions.ILike(partner.CardName!, $"%{term}%")
                    || EF.Functions.ILike(partner.CardCode, $"%{term}%")))
            .OrderBy(partner => partner.CardName)
            .Take(limit * 4)
            .ToListAsync(cancellationToken);

        var results = new List<ShopCandidate>();
        foreach (var row in rows)
        {
            if (excludingRoute is not null && map.IsOnRoute(row.CardCode, excludingRoute))
            {
                continue;
            }

            // Never offer a retired Zimbabwe dollar code to put on a route. The
            // page hides those on the route itself, so offering one means
            // clicking Place and watching nothing appear -- the change is real
            // and staged, and the row it should have made is filtered away.
            if (IsRetiredCurrency(row.Currency))
            {
                continue;
            }

            results.Add(new ShopCandidate(
                row.CardCode.Trim(),
                string.IsNullOrWhiteSpace(row.CardName) ? row.CardCode.Trim() : row.CardName!.Trim(),
                map.GetRoutes(row.CardCode)));

            if (results.Count >= limit)
            {
                break;
            }
        }

        return results;
    }

    public async Task<IReadOnlyList<UnplacedShop>> ListActiveShopsAsync(
        CancellationToken cancellationToken = default)
    {
        // Active customers only, matching how the rest of the Web reads this
        // cache. Code, name and currency: the currency is not shown, it is what
        // keeps retired Zimbabwe dollar accounts out of a list whose whole
        // purpose is choosing a shop to start delivering to.
        var rows = await context.CachedBusinessPartners
            .AsNoTracking()
            .Where(partner => partner.IsActive && partner.CardType == "cCustomer")
            .OrderBy(partner => partner.CardName)
            .Select(partner => new { partner.CardCode, partner.CardName, partner.Currency })
            .ToListAsync(cancellationToken);

        return rows
            .Where(row => !IsRetiredCurrency(row.Currency))
            .Select(row => new UnplacedShop(
                row.CardCode.Trim(),
                string.IsNullOrWhiteSpace(row.CardName) ? row.CardCode.Trim() : row.CardName!.Trim()))
            .ToList();
    }

    /// <summary>
    /// The Zimbabwe dollar, in the spellings the partner master uses. ZiG
    /// replaced it in April 2024 and accounts held in it stopped being invoiced
    /// then.
    /// </summary>
    /// <remarks>
    /// More than one spelling because more than one company database is in
    /// play: production writes "ZWL" and the test company writes "ZW$", so
    /// matching only the one seen locally would quietly do nothing in
    /// production -- the failure that looks exactly like success.
    /// </remarks>
    private static readonly HashSet<string> RetiredCurrencies =
        new(StringComparer.OrdinalIgnoreCase) { "ZWL", "ZW$", "ZWD", "RTGS" };

    internal static bool IsRetiredCurrency(string? currency) =>
        currency is not null && RetiredCurrencies.Contains(currency.Trim());

    public async Task<IReadOnlyDictionary<string, ShopStanding>> GetShopStandingsAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await context.CachedBusinessPartners
            .AsNoTracking()
            .Where(partner => partner.CardType == "cCustomer")
            .Select(partner => new { partner.CardCode, partner.IsActive, partner.Currency })
            .ToListAsync(cancellationToken);

        var standings = new Dictionary<string, ShopStanding>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var frozen = !row.IsActive;
            var retired = IsRetiredCurrency(row.Currency);
            if (!frozen && !retired)
            {
                continue;
            }

            var code = DeliveryRoutes.NormalizeCardCode(row.CardCode);
            if (code.Length > 0)
            {
                standings[code] = new ShopStanding(frozen, retired);
            }
        }

        return standings;
    }

    public async Task DeleteRouteAsync(
        string name, string username, CancellationToken cancellationToken = default)
    {
        name = (name ?? string.Empty).Trim();
        var route = await context.CustomDeliveryRoutes
            .FirstOrDefaultAsync(row => row.Name.ToLower() == name.ToLower(), cancellationToken);

        if (route is null)
        {
            // Either it never existed or it is a workbook route, which cannot be
            // deleted: the next regeneration would bring it straight back.
            throw new InvalidOperationException(
                $"\"{name}\" is not a route that was added here, so it cannot be deleted");
        }

        // The assignments onto it go too. Left behind they would be invisible
        // rows that silently reappear if the same route name were added again.
        var assignments = await context.RouteAssignmentOverrides
            .Where(row => row.RouteName.ToLower() == name.ToLower())
            .ToListAsync(cancellationToken);

        context.RouteAssignmentOverrides.RemoveRange(assignments);
        context.CustomDeliveryRoutes.Remove(route);
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "{User} deleted delivery route {Route} and {Count} assignment(s) onto it",
            username, name, assignments.Count);
    }
}
