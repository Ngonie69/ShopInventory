using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.TransferListener.Queries.GetTransferListenerStatus;

public sealed class GetTransferListenerStatusHandler(
    ITransferListenerService listenerService,
    ILogger<GetTransferListenerStatusHandler> logger
) : IRequestHandler<GetTransferListenerStatusQuery, ErrorOr<TransferListenerStatusModel>>
{
    public async Task<ErrorOr<TransferListenerStatusModel>> Handle(
        GetTransferListenerStatusQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var status = await listenerService.GetStatusAsync(request.RecentDocumentCount, cancellationToken);

            // The API answers 200 with reachable:false when the listener is down, so a null here is
            // the API itself failing us. The page says so in those words rather than blaming the
            // listener for a fault one hop closer to home.
            return status is null
                ? Errors.TransferListener.LoadFailed(
                    "The API did not answer for the transfer listener. This says nothing about the "
                    + "listener itself \u2014 it is the call to the API that failed.")
                : status;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading the transfer listener status");
            return Errors.TransferListener.LoadFailed(ex.Message);
        }
    }
}
