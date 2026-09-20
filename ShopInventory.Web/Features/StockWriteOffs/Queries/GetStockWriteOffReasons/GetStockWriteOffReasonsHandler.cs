using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.StockWriteOffs.Queries.GetStockWriteOffReasons;

public sealed class GetStockWriteOffReasonsHandler(
    IStockWriteOffService writeOffService,
    ILogger<GetStockWriteOffReasonsHandler> logger)
    : IRequestHandler<GetStockWriteOffReasonsQuery, ErrorOr<StockWriteOffReasonsResponse>>
{
    public async Task<ErrorOr<StockWriteOffReasonsResponse>> Handle(
        GetStockWriteOffReasonsQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var (success, message, response) = await writeOffService.GetReasonsAsync();
            return success && response is not null ? response : Errors.StockWriteOff.LoadFailed(message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading stock write-off reasons");
            return Errors.StockWriteOff.LoadFailed("The write-off reasons could not be loaded.");
        }
    }
}
