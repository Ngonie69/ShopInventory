using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Mappings;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.DesktopIntegration.Commands.ConvertTransferRequest;

public sealed class ConvertTransferRequestHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> sapSettings,
    ILogger<ConvertTransferRequestHandler> logger
) : IRequestHandler<ConvertTransferRequestCommand, ErrorOr<InventoryTransferCreatedResponseDto>>
{
    public async Task<ErrorOr<InventoryTransferCreatedResponseDto>> Handle(
        ConvertTransferRequestCommand command,
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

            logger.LogInformation("Desktop app converting transfer request {DocEntry} to transfer", command.DocEntry);

            var transfer = await sapClient.ConvertTransferRequestToTransferAsync(command.DocEntry, cancellationToken);

            return new InventoryTransferCreatedResponseDto
            {
                Message = $"Transfer request {command.DocEntry} converted to transfer successfully",
                Transfer = transfer.ToDto()
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error converting transfer request {DocEntry}", command.DocEntry);
            return Errors.DesktopIntegration.TransferRequestFailed(ex.Message);
        }
    }
}
