using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Sales;
using ShopInventory.Data;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.VanSalesReports.Queries;

/// <summary>What a truck's calls on a day are measured against.</summary>
public static class VanPlanBasis
{
    /// <summary>The shops the round holds in the area worked that day. The CCR's proper denominator.</summary>
    public const string Round = "Round";

    /// <summary>
    /// The truck's whole customer list as counted at Start Day, because nothing narrower is known.
    /// A rate against it reads low and is not a call completion rate; it is shown, and not judged.
    /// </summary>
    public const string List = "List";

    /// <summary>
    /// The days a period's CCR is measured over: those with a round, where any has one, and only
    /// otherwise the days measured against the whole list.
    /// </summary>
    /// <remarks>
    /// Never both. A round is sixty shops and a list is three hundred, so one day on the list among
    /// four on the round would put more into the bottom of the fraction than the other four together,
    /// and the period would read as the list does. The days left out are counted beside the rate
    /// rather than folded into it.
    /// </remarks>
    public static IReadOnlyList<T> DaysToMeasure<T>(IEnumerable<T> days, Func<T, string?> basisOf)
    {
        var all = days.ToList();
        var onRound = all.Where(day => basisOf(day) == Round).ToList();

        return onRound.Count > 0 ? onRound : all;
    }
}

/// <summary>
/// The shops a truck was due to call on, on a given day: the day's round, not its whole list.
/// </summary>
/// <remarks>
/// <para>
/// A truck's customer list is every shop it trades with across the week. West 1 held 286 in October
/// 2026 and calls on about sixty a day, a different sixty on each weekday. The CCR was measured against
/// the count taken at Start Day, which is the whole list, so a truck that reached every shop on its
/// Thursday round scored 21%.
/// </para>
/// <para>
/// The day's round is put together from the two things the system does hold:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>The schedule.</b> The shops the office has given that weekday, in
/// <see cref="RouteCustomerVisitDayEntity"/>.
/// </item>
/// <item>
/// <b>The round as worked.</b> The shops the truck checked into or sold to on an earlier day when it
/// worked the same area, over the last <see cref="LookbackDays"/> days. A town truck's area is its
/// weekday's stops; an upcountry trip's is whatever the rep or the office named.
/// </item>
/// </list>
/// <para>
/// A shop the office has since deactivated is left out, so closing a shop is something the office can
/// do about a round that can no longer be completed. Only earlier days count, never the day itself: a
/// shop first called on today is a new customer, not a planned call.
/// </para>
/// <para>
/// Where neither source names a single shop, the round is not known: the first day ever in an area, or
/// an upcountry day nobody has named. The reports then fall back to the whole list and say so
/// (<see cref="VanPlanBasis.List"/>).
/// </para>
/// </remarks>
public sealed class VanDayPlans
{
    /// <summary>
    /// Thirteen weeks. Long enough to hold three turns of a four-week upcountry cycle, short enough
    /// that a shop the truck stopped calling on a quarter ago is no longer held against it.
    /// </summary>
    public const int LookbackDays = 91;

    /// <summary>The round for one truck-day.</summary>
    /// <param name="AreaName">The area worked, or null when the round comes from the schedule alone.</param>
    /// <param name="Shops">The shops due, as route customer codes.</param>
    public sealed record DayPlan(string? AreaName, IReadOnlySet<string> Shops);

    /// <summary>Where a truck worked on a day, and who said so.</summary>
    public readonly record struct Area(string Name, string Source);

    private readonly VanTruckRoutes _routes;
    private readonly ILookup<int, RouteDayPlan.Stop> _stops;

    // Everything below is keyed on the truck, which is the van account its reps sign in under.
    private readonly Dictionary<string, SortedDictionary<DateTime, HashSet<string>>> _calls = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Truck, DateTime Day), int> _dayRoutes = [];
    private readonly Dictionary<(string Truck, DateTime Day), string> _dayAreas = [];
    private readonly Dictionary<(int RouteId, DateTime Day), string> _officeAreas = [];
    private readonly Dictionary<(string Truck, DayOfWeek Day), HashSet<string>> _scheduled = [];
    private readonly Dictionary<string, HashSet<string>> _deactivated = new(StringComparer.Ordinal);

    private readonly Dictionary<(string Truck, DateTime Day), Area?> _areas = [];
    private readonly Dictionary<(string Truck, DateTime Day), DayPlan?> _plans = [];

    private VanDayPlans(VanTruckRoutes routes, ILookup<int, RouteDayPlan.Stop> stops)
    {
        _routes = routes;
        _stops = stops;
    }

    /// <summary>No history and no schedule: every truck-day falls back to its list.</summary>
    public static VanDayPlans None { get; } =
        new(VanTruckRoutes.None, Array.Empty<RouteDayPlan.Stop>().ToLookup(stop => stop.RouteId));

    /// <summary>
    /// Loads what it takes to say the round for any truck-day from <paramref name="from"/> to
    /// <paramref name="to"/>: the calls and areas of the thirteen weeks before, and the schedule.
    /// </summary>
    public static async Task<VanDayPlans> LoadAsync(
        ApplicationDbContext db,
        DateTime from,
        DateTime to,
        CancellationToken cancellationToken)
    {
        var first = from.Date.AddDays(-LookbackDays);
        var last = to.Date;
        var (windowStartUtc, windowEndUtc) = VanSalesFacts.ToUtcWindow(first, last);

        var routes = await VanTruckRoutes.LoadAsync(db, cancellationToken);

        var routeIds = await db.Routes.AsNoTracking().Select(route => route.Id).ToListAsync(cancellationToken);
        var plans = new VanDayPlans(routes, await RouteDayPlan.LoadAsync(db, routeIds, cancellationToken));

        // Light reads: only who, when and which shop. The amounts are the fact reader's business.
        var visits = await db.TimesheetEntries
            .AsNoTracking()
            .Where(entry => entry.Channel == TimesheetChannel.VanSales
                            && entry.CheckInTime >= windowStartUtc
                            && entry.CheckInTime < windowEndUtc)
            .Select(entry => new { entry.UserId, entry.CheckInTime, entry.CustomerCode })
            .ToListAsync(cancellationToken);

        foreach (var visit in visits)
        {
            plans.AddCall(routes.TruckOf(visit.UserId), VanSalesFacts.TradingDayOf(visit.CheckInTime), visit.CustomerCode);
        }

        var online = await db.StockReservations
            .AsNoTracking()
            .Where(reservation => reservation.SourceSystem == SaleSourceSystems.VanSales
                                  && reservation.Status == ReservationStatus.Confirmed
                                  && reservation.RouteCustomerCode != null
                                  && reservation.CreatedAt >= windowStartUtc
                                  && reservation.CreatedAt < windowEndUtc)
            .Select(reservation => new { reservation.CreatedBy, reservation.CreatedAt, reservation.RouteCustomerCode })
            .ToListAsync(cancellationToken);

        foreach (var sale in online)
        {
            if (VanSalesFacts.TryResolveRep(sale.CreatedBy, out var rep))
            {
                plans.AddCall(routes.TruckOf(rep), VanSalesFacts.TradingDayOf(sale.CreatedAt), sale.RouteCustomerCode);
            }
        }

        var offline = await db.DesktopSales
            .AsNoTracking()
            .Where(sale => sale.SourceSystem == SaleSourceSystems.VanSales
                           && sale.RouteCustomerCode != null
                           && sale.DocDate >= first
                           && sale.DocDate <= last)
            .Select(sale => new { sale.CreatedBy, sale.DocDate, sale.RouteCustomerCode })
            .ToListAsync(cancellationToken);

        foreach (var sale in offline)
        {
            if (VanSalesFacts.TryResolveRep(sale.CreatedBy, out var rep))
            {
                plans.AddCall(routes.TruckOf(rep), sale.DocDate.Date, sale.RouteCustomerCode);
            }
        }

        var routeDays = await db.VanRouteDays
            .AsNoTracking()
            .Where(day => day.TradingDate >= first && day.TradingDate <= last)
            .OrderBy(day => day.DepartedAt)
            .Select(day => new { day.UserId, day.TradingDate, day.RouteId, day.AreaName })
            .ToListAsync(cancellationToken);

        foreach (var day in routeDays)
        {
            var key = (routes.TruckOf(day.UserId), day.TradingDate.Date);

            // The first rep to open the day speaks for the truck, as in VanTruckDays.
            if (day.RouteId is { } routeId)
            {
                plans._dayRoutes.TryAdd(key, routeId);
            }

            if (RouteDayPlan.Clip(day.AreaName) is { } area)
            {
                plans._dayAreas.TryAdd(key, area);
            }
        }

        var entries = await db.VanTruckDayEntries
            .AsNoTracking()
            .Where(entry => entry.AreaName != null && entry.TradingDate >= first && entry.TradingDate <= last)
            .Select(entry => new { entry.RouteId, entry.TradingDate, entry.AreaName })
            .ToListAsync(cancellationToken);

        foreach (var entry in entries)
        {
            if (RouteDayPlan.Clip(entry.AreaName) is { } area)
            {
                plans._officeAreas[(entry.RouteId, entry.TradingDate.Date)] = area;
            }
        }

        var scheduled = await db.RouteCustomerVisitDays
            .AsNoTracking()
            .Where(visitDay => visitDay.RouteCustomer!.IsActive)
            .Select(visitDay => new
            {
                visitDay.RouteCustomer!.AssignedBusinessPartnerCode,
                visitDay.RouteCustomer.Code,
                visitDay.DayOfWeek
            })
            .ToListAsync(cancellationToken);

        foreach (var due in scheduled)
        {
            if (Shop(due.Code) is not { } shop || string.IsNullOrWhiteSpace(due.AssignedBusinessPartnerCode))
            {
                continue;
            }

            var key = (VanTruckRoutes.TruckOfAccount(due.AssignedBusinessPartnerCode), due.DayOfWeek);

            if (!plans._scheduled.TryGetValue(key, out var shops))
            {
                plans._scheduled[key] = shops = new HashSet<string>(StringComparer.Ordinal);
            }

            shops.Add(shop);
        }

        var deactivated = await db.RouteCustomers
            .AsNoTracking()
            .Where(customer => !customer.IsActive)
            .Select(customer => new { customer.AssignedBusinessPartnerCode, customer.Code })
            .ToListAsync(cancellationToken);

        foreach (var gone in deactivated)
        {
            if (Shop(gone.Code) is not { } shop || string.IsNullOrWhiteSpace(gone.AssignedBusinessPartnerCode))
            {
                continue;
            }

            var truck = VanTruckRoutes.TruckOfAccount(gone.AssignedBusinessPartnerCode);

            if (!plans._deactivated.TryGetValue(truck, out var shops))
            {
                plans._deactivated[truck] = shops = new HashSet<string>(StringComparer.Ordinal);
            }

            shops.Add(shop);
        }

        return plans;
    }

    /// <summary>
    /// Where the truck worked on the day: what the office entered, else what the day was opened with,
    /// else the route's stops for the weekday. Null when none of the three names anywhere.
    /// </summary>
    public Area? AreaOf(string truck, DateTime day)
    {
        var key = (truck, day.Date);

        if (_areas.TryGetValue(key, out var known))
        {
            return known;
        }

        var routeId = _dayRoutes.TryGetValue(key, out var opened) ? opened : _routes.RouteOf(truck);
        Area? area = null;

        if (routeId is { } route && _officeAreas.TryGetValue((route, day.Date), out var office))
        {
            area = new Area(office, "Office");
        }
        else if (_dayAreas.TryGetValue(key, out var stamped))
        {
            area = new Area(stamped, "Day");
        }
        else if (routeId is { } planned && RouteDayPlan.AreaFor(_stops[planned], day.DayOfWeek) is { } stops)
        {
            area = new Area(stops, "Plan");
        }

        _areas[key] = area;
        return area;
    }

    /// <summary>The truck's round for the day, or null when no shop can be named as due.</summary>
    public DayPlan? PlanFor(string truck, DateTime day)
    {
        var key = (truck, day.Date);

        if (_plans.TryGetValue(key, out var known))
        {
            return known;
        }

        var shops = new HashSet<string>(StringComparer.Ordinal);

        if (_scheduled.TryGetValue((truck, day.DayOfWeek), out var due))
        {
            shops.UnionWith(due);
        }

        var area = AreaOf(truck, day);

        if (area is { } here && _calls.TryGetValue(truck, out var byDay))
        {
            var areaKey = AreaKey(here.Name);
            var earliest = day.Date.AddDays(-LookbackDays);

            foreach (var (earlier, called) in byDay)
            {
                if (earlier >= day.Date)
                {
                    break;
                }

                if (earlier >= earliest && AreaOf(truck, earlier) is { } then && AreaKey(then.Name) == areaKey)
                {
                    shops.UnionWith(called);
                }
            }
        }

        if (_deactivated.TryGetValue(truck, out var gone))
        {
            shops.ExceptWith(gone);
        }

        var plan = shops.Count == 0 ? null : new DayPlan(area?.Name, shops);
        _plans[key] = plan;
        return plan;
    }

    /// <summary>A shop code as plans hold it. Null for a blank one.</summary>
    public static string? Shop(string? code) =>
        string.IsNullOrWhiteSpace(code) ? null : code.Trim().ToUpperInvariant();

    /// <summary>
    /// What makes two days the same area: the name with case, spaces and punctuation taken out. "Glen
    /// Norah B" and "glen norah b," are one area. "Glenorah" and "Glen Norah" are two, because nothing
    /// here can know they are the same place; the entry form shows the plan's own spelling for that.
    /// </summary>
    public static string AreaKey(string name) =>
        new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private void AddCall(string truck, DateTime day, string? code)
    {
        if (Shop(code) is not { } shop)
        {
            return;
        }

        if (!_calls.TryGetValue(truck, out var byDay))
        {
            _calls[truck] = byDay = [];
        }

        if (!byDay.TryGetValue(day.Date, out var shops))
        {
            byDay[day.Date] = shops = new HashSet<string>(StringComparer.Ordinal);
        }

        shops.Add(shop);
    }
}
