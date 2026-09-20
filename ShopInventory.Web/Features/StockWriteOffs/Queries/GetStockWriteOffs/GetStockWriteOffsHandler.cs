using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.StockWriteOffs.Queries.GetStockWriteOffs;

public sealed class GetStockWriteOffsHandler(
    IStockWriteOffService writeOffService,
    ILogger<GetStockWriteOffsHandler> logger)
    : IRequestHandler<GetStockWriteOffsQuery, ErrorOr<StockWriteOffListResponse>>
{
    public async Task<ErrorOr<StockWriteOffListResponse>> Handle(
        GetStockWriteOffsQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var (success, message, response) = await writeOffService.GetWriteOffsAsync(
                request.Status, request.WarehouseCode, request.Page, request.PageSize);
            return success && response is not null ? response : Errors.StockWriteOff.LoadFailed(message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading stock write-offs");
            return Errors.StockWriteOff.LoadFailed("The write-offs could not be loaded.");
        }
    }
}
