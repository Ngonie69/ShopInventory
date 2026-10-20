using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.FactoryTransfers.Queries.GetDepotOrders;

public sealed class GetDepotOrdersHandler(
    IFactoryTransferService factoryTransferService,
    ILogger<GetDepotOrdersHandler> logger)
    : IRequestHandler<GetDepotOrdersQuery, ErrorOr<List<DepotOrderDto>>>
{
    public async Task<ErrorOr<List<DepotOrderDto>>> Handle(
        GetDepotOrdersQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var (success, message, value) = await factoryTransferService.GetDepotOrdersAsync(request.DepotCode);
            return success && value is not null ? value : Errors.FactoryTransfer.LoadFailed(message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetDepotOrders failed");
            return Errors.FactoryTransfer.LoadFailed("The depot's open orders could not be loaded.");
        }
    }
}
