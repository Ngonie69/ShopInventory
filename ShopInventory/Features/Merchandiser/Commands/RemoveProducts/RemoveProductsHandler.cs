using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;

namespace ShopInventory.Features.Merchandiser.Commands.RemoveProducts;

public sealed class RemoveProductsHandler(
    ApplicationDbContext context,
    ILogger<RemoveProductsHandler> logger
) : IRequestHandler<RemoveProductsCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        RemoveProductsCommand command,
        CancellationToken cancellationToken)
    {
        var products = await context.MerchandiserProducts
            .Where(mp => mp.MerchandiserUserId == command.UserId && command.Request.ItemCodes.Contains(mp.ItemCode))
            .ToListAsync(cancellationToken);

        context.MerchandiserProducts.RemoveRange(products);
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Removed {Count} products from merchandiser {UserId}", products.Count, command.UserId);

        return Result.Success;
    }
}
