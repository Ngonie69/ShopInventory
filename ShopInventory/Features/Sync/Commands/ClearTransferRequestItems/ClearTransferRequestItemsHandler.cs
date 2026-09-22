using ErrorOr;
using MediatR;
using Microsoft.Extensions.Caching.Memory;
using ShopInventory.Features.DesktopIntegration.Queries.GetTransferRequestItems;

namespace ShopInventory.Features.Sync.Commands.ClearTransferRequestItems;

public sealed class ClearTransferRequestItemsHandler(
    IMemoryCache cache,
    ILogger<ClearTransferRequestItemsHandler> logger
) : IRequestHandler<ClearTransferRequestItemsCommand, ErrorOr<Success>>
{
    public Task<ErrorOr<Success>> Handle(
        ClearTransferRequestItemsCommand command,
        CancellationToken cancellationToken)
    {
        cache.Remove(GetTransferRequestItemsHandler.FreshKey);
        logger.LogInformation("Cleared the transfer-request item list; the next till read goes to SAP");
        return Task.FromResult<ErrorOr<Success>>(Result.Success);
    }
}
