using ShopInventory.DTOs;
using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Services;

namespace ShopInventory.Features.DesktopIntegration.Commands.CreateQueuedTransfer;

public sealed class CreateQueuedTransferHandler(
    IInventoryTransferQueueService transferQueueService,
    ILogger<CreateQueuedTransferHandler> logger
) : IRequestHandler<CreateQueuedTransferCommand, ErrorOr<QueuedTransferResponseDto>>
{
    public async Task<ErrorOr<QueuedTransferResponseDto>> Handle(
        CreateQueuedTransferCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = command.Request;

            logger.LogInformation("Desktop app creating queued inventory transfer: From={From}, To={To}",
                request.FromWarehouse, request.ToWarehouse);

            var queueResult = await transferQueueService.EnqueueTransferAsync(
                request,
                null,
                command.CreatedBy,
                cancellationToken);

            if (!queueResult.Success)
                return Errors.DesktopIntegration.TransferFailed(queueResult.ErrorMessage ?? "Failed to queue transfer");

            logger.LogInformation(
                "Inventory transfer queued successfully: ExternalRef={ExternalRef}, QueueId={QueueId}",
                queueResult.ExternalReference, queueResult.QueueId);

            return new QueuedTransferResponseDto
            {
                Success = true,
                Message = "Inventory transfer queued for processing. Poll the status endpoint to check completion.",
                ExternalReference = queueResult.ExternalReference,
                QueueId = queueResult.QueueId,
                Status = "Pending",
                EstimatedProcessingSeconds = 15
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating queued transfer");
            return Errors.DesktopIntegration.TransferFailed(ex.Message);
        }
    }
}
