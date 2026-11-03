using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Web.Data;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.Reports.MonthOnMonth;

/// <summary>
/// Reads the cached product master straight from the Web database.
/// </summary>
/// <remarks>
/// Not through <see cref="IMasterDataCacheService.GetProductsAsync"/>: that returns active items only and
/// leaves <c>ItemsGroupCode</c> off what it projects, and an item retired since still sold in the month and
/// still belongs to its product line. The item group names come through the cache service, which fills its
/// table from the API when it is empty.
/// </remarks>
public sealed class GetMonthOnMonthItemMasterHandler(
    IDbContextFactory<WebAppDbContext> dbContextFactory,
    IMasterDataCacheService masterDataCache)
    : IRequestHandler<GetMonthOnMonthItemMasterQuery, MonthOnMonthItemMaster>
{
    public async Task<MonthOnMonthItemMaster> Handle(GetMonthOnMonthItemMasterQuery request, CancellationToken cancellationToken)
    {
        // Makes sure the products table has been filled at least once (it syncs only when empty).
        await masterDataCache.GetProductsAsync(cancellationToken: cancellationToken);
        var groups = await masterDataCache.GetItemGroupsAsync(cancellationToken: cancellationToken);

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.CachedProducts
            .AsNoTracking()
            .Select(p => new { p.ItemCode, p.ItemName, p.ItemsGroupCode, p.IsActive })
            .ToListAsync(cancellationToken);

        var items = rows
            .Where(p => !string.IsNullOrWhiteSpace(p.ItemCode))
            .GroupBy(p => p.ItemCode.Trim().ToUpperInvariant())
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var best = g.OrderByDescending(p => p.IsActive).First();
                    return new MonthOnMonthItemInfo(best.ItemName, best.ItemsGroupCode);
                },
                StringComparer.Ordinal);

        var groupNames = groups
            .GroupBy(g => g.Number)
            .ToDictionary(g => g.Key, g => g.First().GroupName ?? $"Item group {g.Key}");

        return new MonthOnMonthItemMaster(items, groupNames);
    }
}
