using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Merchandiser.Queries.GetMerchandiserProducts;

public sealed class GetMerchandiserProductsHandler(
    ApplicationDbContext context
) : IRequestHandler<GetMerchandiserProductsQuery, ErrorOr<MerchandiserProductListResponseDto>>
{
    public async Task<ErrorOr<MerchandiserProductListResponseDto>> Handle(
        GetMerchandiserProductsQuery request,
        CancellationToken cancellationToken)
    {
        var user = await context.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == request.UserId && u.Role == "Merchandiser", cancellationToken);

        if (user == null)
            return Errors.Merchandiser.NotFound(request.UserId);

        var products = await context.MerchandiserProducts
            .AsNoTracking()
            .Where(mp => mp.MerchandiserUserId == request.UserId)
            .OrderBy(mp => mp.ItemCode)
            .Select(mp => new MerchandiserProductDto
            {
                Id = mp.Id,
                MerchandiserUserId = mp.MerchandiserUserId,
                ItemCode = mp.ItemCode,
                ItemName = mp.ItemName,
                IsActive = mp.IsActive,
                CreatedAt = mp.CreatedAt,
                UpdatedAt = mp.UpdatedAt,
                UpdatedBy = mp.UpdatedBy
            })
            .ToListAsync(cancellationToken);

        return new MerchandiserProductListResponseDto
        {
            MerchandiserUserId = request.UserId,
            MerchandiserName = $"{user.FirstName} {user.LastName}".Trim(),
            TotalCount = products.Count,
            ActiveCount = products.Count(p => p.IsActive),
            Products = products
        };
    }
}
