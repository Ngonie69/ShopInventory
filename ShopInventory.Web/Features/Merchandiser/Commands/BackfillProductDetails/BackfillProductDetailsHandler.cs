using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.Merchandiser.Commands.BackfillProductDetails;

public sealed class BackfillProductDetailsHandler(
    IMerchandiserService merchandiserService,
    ILogger<BackfillProductDetailsHandler> logger
) : IRequestHandler<BackfillProductDetailsCommand, ErrorOr<int>>
{
    public async Task<ErrorOr<int>> Handle(
        BackfillProductDetailsCommand request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await merchandiserService.BackfillProductDetailsAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error syncing merchandiser product details from SAP");
            return Errors.Merchandiser.BackfillFailed("Failed to sync merchandiser product details.");
        }
    }
}