using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.InventoryTransfers.Commands.CloseTransferRequest;

public sealed class CloseTransferRequestHandler(
    ISAPServiceLayerClient sapClient,
    IAuditService auditService,
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
            await sapClient.CloseInventoryTransferRequestAsync(command.DocEntry, cancellationToken);

            try { await auditService.LogAsync(AuditActions.CloseTransferRequest, "TransferRequest", command.DocEntry.ToString(), $"Transfer request {command.DocEntry} closed", true); } catch { }

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
