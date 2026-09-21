using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Data;

namespace ShopInventory.Web.Features.StockWriteOffs.Queries.GetStockWriteOffItems;

/// <summary>
/// Reads the write-off catalogue out of the Web's <see cref="WebAppDbContext.CachedProducts"/>.
/// </summary>
/// <remarks>
/// That table is the copy of SAP's item master the hourly product sync keeps — valid inventory
/// items only, with the batch flag — so it already holds exactly what the picker shows and
/// nothing has to be asked of the Service Layer to draw it. Three columns over a few thousand
/// rows is a single cheap PostgreSQL read per page visit, so there is no memory cache in front of
/// it to go stale.
/// </remarks>
public sealed class GetStockWriteOffItemsHandler(
    IDbContextFactory<WebAppDbContext> dbContextFactory,
    ILogger<GetStockWriteOffItemsHandler> logger)
    : IRequestHandler<GetStockWriteOffItemsQuery, ErrorOr<GetStockWriteOffItemsResult>>
{
    /// <summary>The sync-info row the product sync stamps; the same key <c>MasterDataCacheService</c> writes.</summary>
    private const string ProductsCacheKey = "Products_All";

    public async Task<ErrorOr<GetStockWriteOffItemsResult>> Handle(
        GetStockWriteOffItemsQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            var items = await db.CachedProducts
                .AsNoTracking()
                .Where(product => product.IsActive)
                .OrderBy(product => product.ItemCode)
                .Select(product => new StockWriteOffItem(
                    product.ItemCode,
                    product.ItemName,
                    product.ManagesBatches))
                .ToListAsync(cancellationToken);

            var syncedAt = await db.CacheSyncInfo
                .AsNoTracking()
                .Where(info => info.CacheKey == ProductsCacheKey && info.SyncSuccessful)
                .Select(info => (DateTime?)info.LastSyncedAt)
                .FirstOrDefaultAsync(cancellationToken);

            return new GetStockWriteOffItemsResult(items, syncedAt);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not read the item catalogue for the write-off page");
            return Errors.StockWriteOff.LoadFailed(
                "The item catalogue could not be read, so nothing can be counted yet.");
        }
    }
}
