using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Shops.Queries.GetShopById;

public sealed class GetShopByIdHandler(ApplicationDbContext context)
    : IRequestHandler<GetShopByIdQuery, ErrorOr<ShopDto>>
{
    public async Task<ErrorOr<ShopDto>> Handle(
        GetShopByIdQuery query,
        CancellationToken cancellationToken)
    {
        var shop = await context.Shops
            .AsNoTracking()
            .Where(candidate => candidate.Id == query.ShopId)
            .Select(ShopMapper.Projection)
            .FirstOrDefaultAsync(cancellationToken);

        return shop is null
            ? Errors.Shops.NotFound(query.ShopId)
            : shop;
    }
}
