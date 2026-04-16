using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.DesktopIntegration.Commands.CloseTransferRequest;

public sealed class CloseTransferRequestHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> sapSettings,
    ILogger<CloseTransferRequestHandler> logger
) : IRequestHandler<CloseTransferRequestCommand, ErrorOr<Deleted>>
{
    public async Task<ErrorOr<Deleted>> Handle(
        CloseTransferRequestCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!sapSettings.Value.Enabled)
                return Errors.DesktopIntegration.SapDisabled;

            var transferRequest = await sapClient.GetInventoryTransferRequestByDocEntryAsync(
                command.DocEntry, cancellationToken);

            if (transferRequest == null)
                return Errors.DesktopIntegration.TransferRequestNotFound(command.DocEntry);

            logger.LogInformation("Desktop app closing transfer request {DocEntry}", command.DocEntry);

            await sapClient.CloseInventoryTransferRequestAsync(command.DocEntry, cancellationToken);

            return Result.Deleted;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error closing transfer request {DocEntry}", command.DocEntry);
            return Errors.DesktopIntegration.TransferRequestFailed(ex.Message);
        }
    }
}
