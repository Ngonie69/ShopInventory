using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Data;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.FactoryTransfers.Commands.SendFactoryTransfer;

public sealed class SendFactoryTransferHandler(
    IFactoryTransferService factoryTransferService,
    IAuditService auditService,
    ILogger<SendFactoryTransferHandler> logger)
    : IRequestHandler<SendFactoryTransferCommand, ErrorOr<FactoryTransferActionResultDto>>
{
    public async Task<ErrorOr<FactoryTransferActionResultDto>> Handle(
        SendFactoryTransferCommand request,
        CancellationToken cancellationToken)
    {
        try
        {
            var (success, message, value) = await factoryTransferService.SendAsync(request.Request);
            await TryAuditAsync(request, value?.Transfer.Summary.Id.ToString(), success, message);
            return success && value is not null ? value : Errors.FactoryTransfer.ActionFailed(message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SendFactoryTransfer failed");
            await TryAuditAsync(request, null, false, ex.Message);
            return Errors.FactoryTransfer.ActionFailed("The transfer could not be sent.");
        }
    }

    private async Task TryAuditAsync(SendFactoryTransferCommand request, string? entityId, bool success, string message)
    {
        try
        {
            await auditService.LogAsync(
                AuditActions.SendFactoryTransfer,
                "FactoryTransfer",
                entityId,
                $"Sent {request.Request.Lines.Count} line(s) from {request.Request.SourceWarehouseCode}" + $". {message}",
                success,
                success ? null : message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to audit SendFactoryTransfer");
        }
    }
}
