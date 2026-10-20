using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.FactoryTransfers.Queries.GetFactoryTransfer;

public sealed class GetFactoryTransferHandler(
    IFactoryTransferService factoryTransferService,
    ILogger<GetFactoryTransferHandler> logger)
    : IRequestHandler<GetFactoryTransferQuery, ErrorOr<FactoryTransferDetailDto>>
{
    public async Task<ErrorOr<FactoryTransferDetailDto>> Handle(
        GetFactoryTransferQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var (success, message, value) = await factoryTransferService.GetTransferAsync(request.Id);
            return success && value is not null ? value : Errors.FactoryTransfer.LoadFailed(message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetFactoryTransfer failed");
            return Errors.FactoryTransfer.LoadFailed("This factory transfer could not be loaded.");
        }
    }
}
