using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;

namespace ShopInventory.Features.ItemVolumeConversions.Commands.DeleteItemVolumeConversion;

public sealed class DeleteItemVolumeConversionHandler(
    ApplicationDbContext context,
    ILogger<DeleteItemVolumeConversionHandler> logger
) : IRequestHandler<DeleteItemVolumeConversionCommand, ErrorOr<Deleted>>
{
    public async Task<ErrorOr<Deleted>> Handle(
        DeleteItemVolumeConversionCommand command,
        CancellationToken cancellationToken)
    {
        var itemCode = command.ItemCode.Trim().ToUpperInvariant();

        var conversion = await context.ItemVolumeConversions
            .FirstOrDefaultAsync(existing => existing.ItemCode == itemCode, cancellationToken);

        if (conversion is null)
        {
            return Errors.ItemVolumeConversion.NotFound(itemCode);
        }

        context.ItemVolumeConversions.Remove(conversion);
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Deleted volume conversion factor for item {ItemCode}", itemCode);

        return Result.Deleted;
    }
}
