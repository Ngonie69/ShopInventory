using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.StockWriteOffs.Queries.GetStockWriteOff;

public sealed class GetStockWriteOffHandler(
    IStockWriteOffService writeOffService,
    ILogger<GetStockWriteOffHandler> logger)
    : IRequestHandler<GetStockWriteOffQuery, ErrorOr<StockWriteOffDetail>>
{
    public async Task<ErrorOr<StockWriteOffDetail>> Handle(
        GetStockWriteOffQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var (success, message, response) = await writeOffService.GetWriteOffAsync(request.WriteOffId);
            return success && response is not null ? response : Errors.StockWriteOff.LoadFailed(message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading stock write-off {Id}", request.WriteOffId);
            return Errors.StockWriteOff.LoadFailed($"Write-off {request.WriteOffId} could not be loaded.");
        }
    }
}
