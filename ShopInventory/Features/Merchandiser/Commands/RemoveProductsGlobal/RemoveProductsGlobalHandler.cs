using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;

namespace ShopInventory.Features.Merchandiser.Commands.RemoveProductsGlobal;

public sealed class RemoveProductsGlobalHandler(
    ApplicationDbContext context,
    ILogger<RemoveProductsGlobalHandler> logger
) : IRequestHandler<RemoveProductsGlobalCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        RemoveProductsGlobalCommand command,
        CancellationToken cancellationToken)
    {
        var products = await context.MerchandiserProducts
            .Where(mp => command.Request.ItemCodes.Contains(mp.ItemCode))
            .ToListAsync(cancellationToken);

        context.MerchandiserProducts.RemoveRange(products);
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Removed {Count} product records globally for {ItemCount} items",
            products.Count, command.Request.ItemCodes.Count);

        return Result.Success;
    }
}
