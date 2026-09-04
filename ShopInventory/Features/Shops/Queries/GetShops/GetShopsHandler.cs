using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Shops.Queries.GetShops;

public sealed class GetShopsHandler(ApplicationDbContext context)
    : IRequestHandler<GetShopsQuery, ErrorOr<List<ShopDto>>>
{
    public async Task<ErrorOr<List<ShopDto>>> Handle(
        GetShopsQuery query,
        CancellationToken cancellationToken)
    {
        var shops = context.Shops.AsNoTracking();

        if (!query.IncludeInactive)
        {
            shops = shops.Where(shop => shop.IsActive);
        }

        return await shops
            .OrderBy(shop => shop.Name)
            .Select(ShopMapper.Projection)
            .ToListAsync(cancellationToken);
    }
}
