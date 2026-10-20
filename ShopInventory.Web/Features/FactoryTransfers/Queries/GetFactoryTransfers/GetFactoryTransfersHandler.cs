using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.FactoryTransfers.Queries.GetFactoryTransfers;

public sealed class GetFactoryTransfersHandler(
    IFactoryTransferService factoryTransferService,
    ILogger<GetFactoryTransfersHandler> logger)
    : IRequestHandler<GetFactoryTransfersQuery, ErrorOr<FactoryTransferListResponseDto>>
{
    public async Task<ErrorOr<FactoryTransferListResponseDto>> Handle(
        GetFactoryTransfersQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var (success, message, value) = await factoryTransferService.GetTransfersAsync(request.View, request.Search, request.Page, request.PageSize);
            return success && value is not null ? value : Errors.FactoryTransfer.LoadFailed(message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetFactoryTransfers failed");
            return Errors.FactoryTransfer.LoadFailed("The factory transfers could not be loaded.");
        }
    }
}
