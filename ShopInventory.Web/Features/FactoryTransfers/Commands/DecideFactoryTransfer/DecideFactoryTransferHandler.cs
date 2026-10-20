using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Data;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.FactoryTransfers.Commands.DecideFactoryTransfer;

public sealed class DecideFactoryTransferHandler(
    IFactoryTransferService factoryTransferService,
    IAuditService auditService,
    ILogger<DecideFactoryTransferHandler> logger)
    : IRequestHandler<DecideFactoryTransferCommand, ErrorOr<FactoryTransferActionResultDto>>
{
    public async Task<ErrorOr<FactoryTransferActionResultDto>> Handle(
        DecideFactoryTransferCommand request,
        CancellationToken cancellationToken)
    {
        try
        {
            var (success, message, value) = await factoryTransferService.DecideAsync(request.Id, request.Request);
            await TryAuditAsync(request, request.Id.ToString(), success, message);
            return success && value is not null ? value : Errors.FactoryTransfer.ActionFailed(message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DecideFactoryTransfer failed");
            await TryAuditAsync(request, null, false, ex.Message);
            return Errors.FactoryTransfer.ActionFailed("Your decision could not be saved.");
        }
    }

    private async Task TryAuditAsync(DecideFactoryTransferCommand request, string? entityId, bool success, string message)
    {
        try
        {
            await auditService.LogAsync(
                AuditActions.DecideFactoryTransfer,
                "FactoryTransfer",
                entityId,
                $"{request.Request.Decision} factory transfer {request.Id}" + $". {message}",
                success,
                success ? null : message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to audit DecideFactoryTransfer");
        }
    }
}
