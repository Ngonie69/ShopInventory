using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Data;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.FactoryTransfers.Commands.RecallFactoryTransfer;

public sealed class RecallFactoryTransferHandler(
    IFactoryTransferService factoryTransferService,
    IAuditService auditService,
    ILogger<RecallFactoryTransferHandler> logger)
    : IRequestHandler<RecallFactoryTransferCommand, ErrorOr<FactoryTransferActionResultDto>>
{
    public async Task<ErrorOr<FactoryTransferActionResultDto>> Handle(
        RecallFactoryTransferCommand request,
        CancellationToken cancellationToken)
    {
        try
        {
            var (success, message, value) = await factoryTransferService.RecallAsync(request.Id, request.Reason.Trim());
            await TryAuditAsync(request, request.Id.ToString(), success, message);
            return success && value is not null ? value : Errors.FactoryTransfer.ActionFailed(message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "RecallFactoryTransfer failed");
            await TryAuditAsync(request, null, false, ex.Message);
            return Errors.FactoryTransfer.ActionFailed("The transfer could not be recalled.");
        }
    }

    private async Task TryAuditAsync(RecallFactoryTransferCommand request, string? entityId, bool success, string message)
    {
        try
        {
            await auditService.LogAsync(
                AuditActions.RecallFactoryTransfer,
                "FactoryTransfer",
                entityId,
                $"Recalled factory transfer {request.Id}" + $". {message}",
                success,
                success ? null : message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to audit RecallFactoryTransfer");
        }
    }
}
