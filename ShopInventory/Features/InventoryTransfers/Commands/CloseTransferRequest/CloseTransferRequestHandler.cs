using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Features.Notifications;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.InventoryTransfers.Commands.CloseTransferRequest;

public sealed class CloseTransferRequestHandler(
    ISAPServiceLayerClient sapClient,
    IAuditService auditService,
    INotificationService notificationService,
    IOptions<SAPSettings> settings,
    ILogger<CloseTransferRequestHandler> logger
) : IRequestHandler<CloseTransferRequestCommand, ErrorOr<object>>
{
    public async Task<ErrorOr<object>> Handle(
        CloseTransferRequestCommand command,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.InventoryTransfer.SapDisabled;

        try
        {
            InventoryTransferRequest? transferRequest = null;
            try
            {
                transferRequest = await sapClient.GetInventoryTransferRequestByDocEntryAsync(command.DocEntry, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to load transfer request {DocEntry} context before close", command.DocEntry);
            }

            await sapClient.CloseInventoryTransferRequestAsync(command.DocEntry, cancellationToken);

            try { await auditService.LogAsync(AuditActions.CloseTransferRequest, "TransferRequest", command.DocEntry.ToString(), $"Transfer request {command.DocEntry} closed", true); } catch { }

            try
            {
                var requestLabel = transferRequest?.DocNum.ToString() ?? command.DocEntry.ToString();
                var fromWarehouse = transferRequest?.FromWarehouse ?? "unspecified";
                var toWarehouse = transferRequest?.ToWarehouse ?? "unknown";

                await notificationService.CreateNotificationAsync(
                    ModuleNotificationFactory.CreateBroadcastNotification(
                        $"Transfer Request Closed: #{requestLabel}",
                        $"Transfer request #{requestLabel} from {fromWarehouse} to {toWarehouse} was closed.",
                        "Warning",
                        "TransferRequest",
                        "TransferRequest",
                        command.DocEntry.ToString(),
                        "/inventory-transfers",
                        new Dictionary<string, string>
                        {
                            ["requestDocEntry"] = command.DocEntry.ToString(),
                            ["requestDocNum"] = transferRequest?.DocNum.ToString() ?? string.Empty,
                            ["fromWarehouse"] = fromWarehouse,
                            ["toWarehouse"] = toWarehouse,
                            ["action"] = "Closed",
                            ["status"] = "Closed"
                        }),
                    cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to publish close notification for transfer request {DocEntry}", command.DocEntry);
            }

            return new { Message = $"Transfer request {command.DocEntry} closed successfully" };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Errors.InventoryTransfer.CreationFailed("Request was canceled by the client");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error closing transfer request {DocEntry}", command.DocEntry);
            return Errors.InventoryTransfer.CreationFailed(ex.Message);
        }
    }
}
