using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.StockWriteOffs.Commands.CreateStockWriteOff;

public sealed class CreateStockWriteOffHandler(
    IStockWriteOffService writeOffService,
    ILogger<CreateStockWriteOffHandler> logger)
    : IRequestHandler<CreateStockWriteOffCommand, ErrorOr<StockWriteOffResult>>
{
    public async Task<ErrorOr<StockWriteOffResult>> Handle(
        CreateStockWriteOffCommand request,
        CancellationToken cancellationToken)
    {
        try
        {
            var (success, message, response) = await writeOffService.CreateAsync(request.Request);
            return success && response is not null ? response : Errors.StockWriteOff.PostFailed(message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error posting a stock write-off");
            return Errors.StockWriteOff.PostFailed("The stock could not be written off.");
        }
    }
}
