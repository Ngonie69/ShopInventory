using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Data;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.FactoryTransfers.Commands.RetryFactoryTransfer;

public sealed class RetryFactoryTransferHandler(
    IFactoryTransferService factoryTransferService,
    IAuditService auditService,
    ILogger<RetryFactoryTransferHandler> logger)
    : IRequestHandler<RetryFactoryTransferCommand, ErrorOr<FactoryTransferActionResultDto>>
{
    public async Task<ErrorOr<FactoryTransferActionResultDto>> Handle(
        RetryFactoryTransferCommand request,
        CancellationToken cancellationToken)
    {
        try
        {
            var (success, message, value) = await factoryTransferService.RetryAsync(request.Id);
            await TryAuditAsync(request, request.Id.ToString(), success, message);
            return success && value is not null ? value : Errors.FactoryTransfer.ActionFailed(message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "RetryFactoryTransfer failed");
            await TryAuditAsync(request, null, false, ex.Message);
            return Errors.FactoryTransfer.ActionFailed("The transfer could not be retried.");
        }
    }

    private async Task TryAuditAsync(RetryFactoryTransferCommand request, string? entityId, bool success, string message)
    {
        try
        {
            await auditService.LogAsync(
                AuditActions.RetryFactoryTransfer,
                "FactoryTransfer",
                entityId,
                $"Retried factory transfer {request.Id}" + $". {message}",
                success,
                success ? null : message);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to audit RetryFactoryTransfer");
        }
    }
}
