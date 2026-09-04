using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Shops.Commands.SetShopActive;

public sealed class SetShopActiveHandler(
    ApplicationDbContext context,
    ILogger<SetShopActiveHandler> logger
) : IRequestHandler<SetShopActiveCommand, ErrorOr<ShopDto>>
{
    public async Task<ErrorOr<ShopDto>> Handle(
        SetShopActiveCommand command,
        CancellationToken cancellationToken)
    {
        var shop = await context.Shops
            .FirstOrDefaultAsync(candidate => candidate.Id == command.ShopId, cancellationToken);

        if (shop is null)
        {
            return Errors.Shops.NotFound(command.ShopId);
        }

        if (shop.IsActive == command.IsActive)
        {
            // Already in the requested state. Returned rather than refused so a double-click on the
            // close button is not an error the administrator has to read and dismiss.
            return await Project(shop.Id, cancellationToken);
        }

        if (!command.IsActive)
        {
            // Counted rather than merely detected, because the number is what tells an administrator
            // how much reassignment work closing this shop actually is.
            var assignedOperators = await context.Users
                .AsNoTracking()
                .CountAsync(user => user.ShopId == shop.Id && user.IsActive, cancellationToken);

            if (assignedOperators > 0)
            {
                return Errors.Shops.HasAssignedOperators(shop.Name, assignedOperators);
            }
        }

        shop.IsActive = command.IsActive;
        shop.UpdatedByUserId = command.UserId;
        shop.UpdatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync(cancellationToken);

        logger.LogWarning(
            "Shop {ShopCode} ({ShopName}) {Transition} by {UserId}",
            shop.Code, shop.Name, command.IsActive ? "reopened" : "closed", command.UserId);

        return await Project(shop.Id, cancellationToken);
    }

    private Task<ShopDto> Project(int shopId, CancellationToken cancellationToken) =>
        context.Shops
            .AsNoTracking()
            .Where(shop => shop.Id == shopId)
            .Select(ShopMapper.Projection)
            .FirstAsync(cancellationToken);
}
