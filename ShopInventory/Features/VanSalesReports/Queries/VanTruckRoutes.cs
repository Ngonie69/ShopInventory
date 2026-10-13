using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;

namespace ShopInventory.Features.VanSalesReports.Queries;

/// <summary>
/// Which truck a rep is on and which route a truck runs. The one statement of both, so the plans and
/// the truck report cannot place a day on different routes.
/// </summary>
public sealed class VanTruckRoutes
{
    private readonly Dictionary<Guid, string?> _accounts;
    private readonly Dictionary<string, int> _routeOfTruck;

    private VanTruckRoutes(Dictionary<Guid, string?> accounts, Dictionary<string, int> routeOfTruck)
    {
        _accounts = accounts;
        _routeOfTruck = routeOfTruck;
    }

    public static VanTruckRoutes None { get; } = new([], []);

    /// <summary>Everyone who could be on a truck: a login with a van account, a route, or both.</summary>
    public static async Task<VanTruckRoutes> LoadAsync(ApplicationDbContext db, CancellationToken cancellationToken)
    {
        var reps = await db.Users
            .AsNoTracking()
            .Where(user => user.RouteId != null
                           || (user.AssignedBusinessPartnerCode != null && user.AssignedBusinessPartnerCode != ""))
            .Select(user => new { user.Id, user.RouteId, user.AssignedBusinessPartnerCode })
            .ToListAsync(cancellationToken);

        var accounts = reps.ToDictionary(rep => rep.Id, rep => rep.AssignedBusinessPartnerCode);

        // The route a truck runs when its day says nothing: the one most of its reps are assigned to.
        var routeOfTruck = reps
            .Where(rep => rep.RouteId.HasValue)
            .GroupBy(rep => VanTruckDays.TruckOf(rep.Id, accounts))
            .ToDictionary(
                truck => truck.Key,
                truck => truck
                    .GroupBy(rep => rep.RouteId!.Value)
                    .OrderByDescending(route => route.Count())
                    .ThenBy(route => route.Key)
                    .First().Key);

        return new VanTruckRoutes(accounts, routeOfTruck);
    }

    public IReadOnlyDictionary<Guid, string?> Accounts => _accounts;

    /// <summary>The truck a rep works on. See <see cref="VanTruckDays.TruckOf"/>.</summary>
    public string TruckOf(Guid userId) => VanTruckDays.TruckOf(userId, _accounts);

    /// <summary>The truck key of a van account, as <see cref="VanTruckDays.TruckOf"/> writes it.</summary>
    public static string TruckOfAccount(string account) => account.Trim().ToUpperInvariant();

    /// <summary>The route the truck's reps are assigned to, or null when none of them is on one.</summary>
    public int? RouteOf(string truck) => _routeOfTruck.TryGetValue(truck, out var route) ? route : null;
}
