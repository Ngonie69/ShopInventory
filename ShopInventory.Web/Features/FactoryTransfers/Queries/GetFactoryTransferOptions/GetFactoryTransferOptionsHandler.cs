using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.FactoryTransfers.Queries.GetFactoryTransferOptions;

public sealed class GetFactoryTransferOptionsHandler(
    IFactoryTransferService factoryTransferService,
    ILogger<GetFactoryTransferOptionsHandler> logger)
    : IRequestHandler<GetFactoryTransferOptionsQuery, ErrorOr<FactoryTransferOptionsDto>>
{
    public async Task<ErrorOr<FactoryTransferOptionsDto>> Handle(
        GetFactoryTransferOptionsQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var (success, message, value) = await factoryTransferService.GetOptionsAsync();
            return success && value is not null ? value : Errors.FactoryTransfer.LoadFailed(message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GetFactoryTransferOptions failed");
            return Errors.FactoryTransfer.LoadFailed("Your factory warehouses could not be loaded.");
        }
    }
}
