using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;

namespace ShopInventory.Features.Merchandiser.Commands.UpdateProductStatus;

public sealed class UpdateProductStatusHandler(
    ApplicationDbContext context,
    ILogger<UpdateProductStatusHandler> logger
) : IRequestHandler<UpdateProductStatusCommand, ErrorOr<int>>
{
    public async Task<ErrorOr<int>> Handle(
        UpdateProductStatusCommand command,
        CancellationToken cancellationToken)
    {
        var user = await context.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == command.UserId && u.Role == "Merchandiser", cancellationToken);

        if (user == null)
            return Errors.Merchandiser.NotFound(command.UserId);

        var now = DateTime.UtcNow;

        var rowsAffected = await context.MerchandiserProducts
            .Where(mp => mp.MerchandiserUserId == command.UserId && command.Request.ItemCodes.Contains(mp.ItemCode))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(mp => mp.IsActive, command.Request.IsActive)
                .SetProperty(mp => mp.UpdatedAt, now)
                .SetProperty(mp => mp.UpdatedBy, command.Username),
                cancellationToken);

        logger.LogInformation("Updated {RowsAffected} products to {Status} for merchandiser {UserId}",
            rowsAffected, command.Request.IsActive ? "active" : "inactive", command.UserId);

        return rowsAffected;
    }
}
