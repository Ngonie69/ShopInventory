using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Features.Notifications;
using ShopInventory.Mappings;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.InventoryTransfers.Commands.ConvertTransferRequest;

public sealed class ConvertTransferRequestHandler(
    ISAPServiceLayerClient sapClient,
    IAuditService auditService,
    INotificationService notificationService,
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

            InventoryTransferRequest? transferRequest = null;
            try
            {
                transferRequest = await sapClient.GetInventoryTransferRequestByDocEntryAsync(command.DocEntry, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to load transfer request {DocEntry} context before conversion", command.DocEntry);
            }

            var transfer = await sapClient.ConvertTransferRequestToTransferAsync(command.DocEntry, cancellationToken);
            var transferDto = transfer.ToDto();

            logger.LogInformation("Transfer request {DocEntry} converted successfully to transfer {TransferDocEntry}",
                command.DocEntry, transfer.DocEntry);

            try { await auditService.LogAsync(AuditActions.ConvertTransferRequest, "TransferRequest", command.DocEntry.ToString(), $"Transfer request {command.DocEntry} converted to transfer {transfer.DocEntry}", true); } catch { }

            try
            {
                var requestLabel = transferRequest?.DocNum.ToString() ?? command.DocEntry.ToString();
                var fromWarehouse = transferRequest?.FromWarehouse ?? "unspecified";
                var toWarehouse = transferRequest?.ToWarehouse ?? "unknown";

                await notificationService.CreateNotificationAsync(
                    ModuleNotificationFactory.CreateBroadcastNotification(
                        $"Transfer Request Converted: #{requestLabel}",
                        $"Transfer request #{requestLabel} from {fromWarehouse} to {toWarehouse} was converted to inventory transfer #{transfer.DocNum}.",
                        "Success",
                        "TransferRequest",
                        "TransferRequest",
                        command.DocEntry.ToString(),
                        "/inventory-transfers",
                        new Dictionary<string, string>
                        {
                            ["requestDocEntry"] = command.DocEntry.ToString(),
                            ["requestDocNum"] = transferRequest?.DocNum.ToString() ?? string.Empty,
                            ["transferDocEntry"] = transfer.DocEntry.ToString(),
                            ["transferDocNum"] = transfer.DocNum.ToString(),
                            ["fromWarehouse"] = fromWarehouse,
                            ["toWarehouse"] = toWarehouse,
                            ["action"] = "Converted"
                        }),
                    cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to publish conversion notification for transfer request {DocEntry}", command.DocEntry);
            }

            return new TransferRequestConvertedResponseDto
            {
                Message = $"Transfer request converted successfully to Inventory Transfer #{transfer.DocNum}",
                RequestDocEntry = command.DocEntry,
                Transfer = transferDto
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
