using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Mappings;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.InventoryTransfers.Commands.ConvertTransferRequest;

public sealed class ConvertTransferRequestHandler(
    ISAPServiceLayerClient sapClient,
    IAuditService auditService,
    IOptions<SAPSettings> settings,
    ILogger<ConvertTransferRequestHandler> logger
) : IRequestHandler<ConvertTransferRequestCommand, ErrorOr<TransferRequestConvertedResponseDto>>
{
    public async Task<ErrorOr<TransferRequestConvertedResponseDto>> Handle(
        ConvertTransferRequestCommand command,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.InventoryTransfer.SapDisabled;

        try
        {
            logger.LogInformation("Converting transfer request {DocEntry} to inventory transfer", command.DocEntry);

            var transfer = await sapClient.ConvertTransferRequestToTransferAsync(command.DocEntry, cancellationToken);

            logger.LogInformation("Transfer request {DocEntry} converted successfully to transfer {TransferDocEntry}",
                command.DocEntry, transfer.DocEntry);

            try { await auditService.LogAsync(AuditActions.ConvertTransferRequest, "TransferRequest", command.DocEntry.ToString(), $"Transfer request {command.DocEntry} converted to transfer {transfer.DocEntry}", true); } catch { }

            return new TransferRequestConvertedResponseDto
            {
                Message = $"Transfer request converted successfully to Inventory Transfer #{transfer.DocNum}",
                RequestDocEntry = command.DocEntry,
                Transfer = transfer.ToDto()
            };
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "Transfer request {DocEntry} not found", command.DocEntry);
            return Errors.InventoryTransfer.TransferRequestNotFound(command.DocEntry);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Cannot convert transfer request {DocEntry}", command.DocEntry);
            return Errors.InventoryTransfer.InvalidOperation(ex.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Errors.InventoryTransfer.CreationFailed("Request was canceled by the client");
        }
        catch (OperationCanceledException ex)
        {
            logger.LogError(ex, "Timeout or connection abort converting transfer request");
            return Errors.InventoryTransfer.SapTimeout;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Network error connecting to SAP Service Layer");
            return Errors.InventoryTransfer.SapConnectionError(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error converting transfer request {DocEntry}", command.DocEntry);
            return Errors.InventoryTransfer.CreationFailed(ex.Message);
        }
    }
}
