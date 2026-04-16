using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;

namespace ShopInventory.Features.Merchandiser.Commands.UpdateProductStatusGlobal;

public sealed class UpdateProductStatusGlobalHandler(
    ApplicationDbContext context,
    ILogger<UpdateProductStatusGlobalHandler> logger
) : IRequestHandler<UpdateProductStatusGlobalCommand, ErrorOr<int>>
{
    public async Task<ErrorOr<int>> Handle(
        UpdateProductStatusGlobalCommand command,
        CancellationToken cancellationToken)
    {
        if (command.Request.ItemCodes.Count == 0)
            return Errors.Merchandiser.AssignmentFailed("No item codes provided");

        var now = DateTime.UtcNow;

        var rowsAffected = await context.MerchandiserProducts
            .Where(mp => command.Request.ItemCodes.Contains(mp.ItemCode))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(mp => mp.IsActive, command.Request.IsActive)
                .SetProperty(mp => mp.UpdatedAt, now)
                .SetProperty(mp => mp.UpdatedBy, command.Username),
                cancellationToken);

        logger.LogInformation("Updated {RowsAffected} product records to {Status} globally for item codes [{Codes}]",
            rowsAffected, command.Request.IsActive ? "active" : "inactive",
            string.Join(", ", command.Request.ItemCodes));

        return rowsAffected;
    }
}
