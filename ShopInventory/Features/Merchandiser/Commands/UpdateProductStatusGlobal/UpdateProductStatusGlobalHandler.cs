using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.Features.Merchandiser.Events.ProductCatalogChanged;

namespace ShopInventory.Features.Merchandiser.Commands.UpdateProductStatusGlobal;

public sealed class UpdateProductStatusGlobalHandler(
    ApplicationDbContext context,
    IPublisher publisher,
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

        // Only the rows this actually flips. The update used to match on item code alone, so setting
        // an item to the status it already holds still counted as work done, restamped UpdatedBy on
        // records nobody had touched, and published a catalogue change that pushed
        // "Item X was deactivated" to every merchandiser's phone again. Any bulk operation that
        // swept an already-inactive item up with the rest re-announced it. A no-op is not news.
        var pendingChanges = context.MerchandiserProducts
            .Where(mp => command.Request.ItemCodes.Contains(mp.ItemCode) &&
                         mp.IsActive != command.Request.IsActive);

        var changedItemCodes = await pendingChanges
            .Select(mp => mp.ItemCode)
            .Distinct()
            .ToListAsync(cancellationToken);

        var rowsAffected = await pendingChanges
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(mp => mp.IsActive, command.Request.IsActive)
                .SetProperty(mp => mp.UpdatedAt, now)
                .SetProperty(mp => mp.UpdatedBy, command.Username),
                cancellationToken);

        logger.LogInformation("Updated {RowsAffected} product records to {Status} globally for item codes [{Codes}]",
            rowsAffected, command.Request.IsActive ? "active" : "inactive",
            string.Join(", ", command.Request.ItemCodes));

        if (changedItemCodes.Count > 0)
        {
            await publisher.Publish(
                new ProductCatalogChangedEvent(
                    changedItemCodes
                        .Where(code => !string.IsNullOrWhiteSpace(code))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    command.Request.IsActive,
                    command.Username,
                    now),
                cancellationToken);
        }

        return rowsAffected;
    }
}
