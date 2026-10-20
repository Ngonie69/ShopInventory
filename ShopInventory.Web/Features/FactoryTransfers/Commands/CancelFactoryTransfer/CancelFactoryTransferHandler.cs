using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Data;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.FactoryTransfers.Commands.CancelFactoryTransfer;

public sealed class CancelFactoryTransferHandler(
    IFactoryTransferService factoryTransferService,
    IAuditService auditService,
    ILogger<CancelFactoryTransferHandler> logger)
    : IRequestHandler<CancelFactoryTransferCommand, ErrorOr<FactoryTransferActionResultDto>>
{
    public async Task<ErrorOr<FactoryTransferActionResultDto>> Handle(
        CancelFactoryTransferCommand request,
        CancellationToken cancellationToken)
    {
        try
        {
            var (success, message, value) = await factoryTransferService.CancelAsync(request.Id);
            await TryAuditAsync(request, request.Id.ToString(), success, message);
            return success && value is not null ? value : Errors.FactoryTransfer.ActionFailed(message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "CancelFactoryTransfer failed");
            await TryAuditAsync(request, null, false, ex.Message);
            return Errors.FactoryTransfer.ActionFailed("The transfer could not be cancelled.");
        }
    }

    private async Task TryAuditAsync(CancelFactoryTransferCommand request, string? entityId, bool success, string message)
    {
        try
        {
            await auditService.LogAsync(
                AuditActions.CancelFactoryTransfer,
                "FactoryTransfer",
                entityId,
                $"Cancelled factory transfer {request.Id}" + $". {message}",
                success,
                success ? null : message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to audit CancelFactoryTransfer");
        }
    }
}
