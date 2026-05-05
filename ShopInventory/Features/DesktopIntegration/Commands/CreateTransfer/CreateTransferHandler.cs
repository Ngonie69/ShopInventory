using ErrorOr;
using MediatR;
using ShopInventory.Common.Validation;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Mappings;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.DesktopIntegration.Commands.CreateTransfer;

public sealed class CreateTransferHandler(
    ApplicationDbContext context,
    ISAPServiceLayerClient sapClient,
    IInventoryTransferQueueService transferQueueService,
    IStockValidationService stockValidation,
    SapCircuitBreakerState sapCircuitBreakerState,
    IOptions<SAPSettings> sapSettings,
    ILogger<CreateTransferHandler> logger
) : IRequestHandler<CreateTransferCommand, ErrorOr<InventoryTransferCreatedResponseDto>>
{
    public async Task<ErrorOr<InventoryTransferCreatedResponseDto>> Handle(
        CreateTransferCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!sapSettings.Value.Enabled)
                return Errors.DesktopIntegration.SapDisabled;

            var request = command.Request;

            logger.LogInformation("Desktop app creating direct transfer: From={From}, To={To}, Lines={Lines}",
                request.FromWarehouse, request.ToWarehouse, request.Lines.Count);

            var sapRequest = new CreateInventoryTransferRequest
            {
                FromWarehouse = request.FromWarehouse,
                ToWarehouse = request.ToWarehouse,
                DocDate = request.DocDate,
                DueDate = request.DueDate,
                Comments = request.Comments,
                Lines = request.Lines.Select(l => new CreateInventoryTransferLineRequest
                {
                    ItemCode = l.ItemCode,
                    Quantity = l.Quantity,
                    UoMCode = l.UoMCode,
                    FromWarehouseCode = l.FromWarehouseCode ?? request.FromWarehouse,
                    ToWarehouseCode = l.WarehouseCode ?? request.ToWarehouse,
                    BatchNumbers = l.BatchNumbers,
                }).ToList()
            };

            var quantityErrors = await UomQuantityValidation.ValidateAndNormalizeLineQuantitiesAsync(
                context,
                sapRequest.Lines,
                line => line.ItemCode,
                line => line.Quantity,
                line => line.UoMCode,
                (line, uomCode) => line.UoMCode = uomCode,
                cancellationToken);

            if (quantityErrors.Count > 0)
                return Errors.DesktopIntegration.ValidationFailed(string.Join("; ", quantityErrors));

            if (sapCircuitBreakerState.IsOpen)
            {
                return await QueueTransferFallbackAsync(request, null, cancellationToken);
            }

            var validationResult = await stockValidation.ValidateInventoryTransferStockAsync(
                sapRequest, cancellationToken);

            if (!validationResult.IsValid)
                return Errors.DesktopIntegration.ValidationFailed(
                    string.Join("; ", validationResult.Errors.Select(e => e.Message)));

            var transfer = await sapClient.CreateInventoryTransferAsync(sapRequest, cancellationToken);

            return new InventoryTransferCreatedResponseDto { Transfer = transfer.ToDto() };
        }
        catch (Exception ex) when (SapFailureClassifier.IsTransient(ex, cancellationToken))
        {
            logger.LogWarning(ex, "SAP is unavailable while creating direct transfer. Falling back to queue.");
            return await QueueTransferFallbackAsync(command.Request, null, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating direct transfer");
            return Errors.DesktopIntegration.TransferFailed(ex.Message);
        }
    }

    private async Task<ErrorOr<InventoryTransferCreatedResponseDto>> QueueTransferFallbackAsync(
        CreateDesktopTransferRequest request,
        string? createdBy,
        CancellationToken cancellationToken)
    {
        var queueableRequest = new CreateDesktopTransferRequest
        {
            ExternalReference = request.ExternalReference,
            SourceSystem = request.SourceSystem ?? "DESKTOP_APP",
            FromWarehouse = request.FromWarehouse,
            ToWarehouse = request.ToWarehouse,
            DocDate = request.DocDate,
            DueDate = request.DueDate,
            Comments = request.Comments,
            JournalMemo = request.JournalMemo,
            Priority = request.Priority,
            IsTransferRequest = false,
            Lines = request.Lines.Select(line => new CreateDesktopTransferLineRequest
            {
                LineNum = line.LineNum,
                ItemCode = line.ItemCode,
                ItemDescription = line.ItemDescription,
                Quantity = line.Quantity,
                UoMCode = line.UoMCode,
                FromWarehouseCode = line.FromWarehouseCode,
                WarehouseCode = line.WarehouseCode,
                AutoAllocateBatches = line.AutoAllocateBatches,
                BatchNumbers = line.BatchNumbers?.Select(batch => new TransferBatchRequest
                {
                    BatchNumber = batch.BatchNumber,
                    Quantity = batch.Quantity
                }).ToList()
            }).ToList()
        };

        var queueResult = await transferQueueService.EnqueueTransferAsync(
            queueableRequest,
            null,
            createdBy,
            cancellationToken);

        if (!queueResult.Success)
        {
            return Errors.DesktopIntegration.TransferFailed(
                queueResult.ErrorMessage ?? "SAP is unavailable and transfer queue fallback failed");
        }

        return new InventoryTransferCreatedResponseDto
        {
            Message = "SAP is currently unavailable. The inventory transfer has been queued for deferred processing.",
            WasQueued = true,
            QueueId = queueResult.QueueId,
            QueueStatus = queueResult.Status,
            QueueExternalReference = queueResult.ExternalReference,
            EstimatedProcessingSeconds = queueResult.EstimatedProcessingTime.HasValue
                ? (int)Math.Ceiling(queueResult.EstimatedProcessingTime.Value.TotalSeconds)
                : null
        };
    }
}
