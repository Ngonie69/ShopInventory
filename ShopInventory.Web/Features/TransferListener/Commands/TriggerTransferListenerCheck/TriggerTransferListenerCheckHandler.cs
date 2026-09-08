using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.TransferListener.Commands.TriggerTransferListenerCheck;

public sealed class TriggerTransferListenerCheckHandler(
    ITransferListenerService listenerService,
    ILogger<TriggerTransferListenerCheckHandler> logger
) : IRequestHandler<TriggerTransferListenerCheckCommand, ErrorOr<TransferListenerCheckModel>>
{
    public async Task<ErrorOr<TransferListenerCheckModel>> Handle(
        TriggerTransferListenerCheckCommand request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await listenerService.TriggerCheckAsync(cancellationToken);

            return result is null
                ? Errors.TransferListener.CheckFailed(
                    "The check was not run. Either the API refused it or the listener could not be reached.")
                : result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error triggering a transfer listener check");
            return Errors.TransferListener.CheckFailed(ex.Message);
        }
    }
}
