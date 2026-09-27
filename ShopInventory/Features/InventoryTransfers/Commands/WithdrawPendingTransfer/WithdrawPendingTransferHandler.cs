using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Idempotency;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.InventoryTransfers.Commands.WithdrawPendingTransfer;

public sealed class WithdrawPendingTransferHandler(
    ApplicationDbContext context,
    ITransferWarehouseAuthorizer warehouseAuthorizer,
    IIdempotencyRequestStore idempotencyRequestStore,
    IAuditService auditService,
    ILogger<WithdrawPendingTransferHandler> logger)
    : IRequestHandler<WithdrawPendingTransferCommand, ErrorOr<PendingInventoryTransferDecisionResponseDto>>
{
    public async Task<ErrorOr<PendingInventoryTransferDecisionResponseDto>> Handle(
        WithdrawPendingTransferCommand command,
        CancellationToken cancellationToken)
    {
        var pending = await context.PendingInventoryTransfers
            .AsTracking()
            .FirstOrDefaultAsync(item => item.Id == command.PendingTransferId, cancellationToken);
        if (pending is null)
            return Errors.InventoryTransfer.PendingTransferNotFound(command.PendingTransferId);

        if (pending.Status != PendingInventoryTransferStatuses.PostFailed)
            return Errors.InventoryTransfer.WithdrawalNotAllowed(pending.Status);

        var scopeCheck = await warehouseAuthorizer.EnsureCanActOnSourceAsync(
            command.UserId, pending.FromWarehouse, cancellationToken);
        if (scopeCheck.IsError)
            return scopeCheck.Errors;

        // A retry leaves the record reading PostFailed for as long as it runs in SAP. Taking the
        // poster's own claim is what stops a withdrawal landing on a transfer that is posting at
        // that moment and would then read Cancelled with a SAP document behind it.
        var acquired = await idempotencyRequestStore.TryAcquireAsync<InventoryTransferDto>(
            PendingInventoryTransferPoster.IdempotencyScope,
            pending.Id.ToString(),
            new { PendingTransferId = pending.Id },
            cancellationToken);
        switch (acquired.Outcome)
        {
            case IdempotencyAcquireOutcome.InProgress:
                return Errors.InventoryTransfer.PostInProgress;
            case IdempotencyAcquireOutcome.ReplayAvailable:
                // A post completed and the record has not caught up; it is in SAP, not withdrawable.
                return Errors.InventoryTransfer.PendingTransferNotActionable(PendingInventoryTransferStatuses.Posted);
            case IdempotencyAcquireOutcome.RequestMismatch:
                return Errors.Idempotency.RequestMismatch("inventory transfer withdrawal");
        }

        try
        {
            pending.Status = PendingInventoryTransferStatuses.Cancelled;
            pending.WithdrawnAtUtc = DateTime.UtcNow;
            pending.WithdrawnByUserId = command.UserId;
            pending.WithdrawalReason = command.Reason.Trim();
            await context.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            if (acquired.RequestId is { } requestId)
            {
                try { await idempotencyRequestStore.ReleaseAsync(requestId, CancellationToken.None); }
                catch (Exception exception)
                {
                    logger.LogWarning(exception,
                        "Failed to release the post claim after withdrawing pending inventory transfer {PendingId}", pending.Id);
                }
            }
        }

        logger.LogInformation(
            "Pending inventory transfer {PendingId} from {FromWarehouse} to {ToWarehouse} withdrawn after a failed post",
            pending.Id, pending.FromWarehouse, pending.ToWarehouse);

        try
        {
            await auditService.LogAsync(
                AuditActions.WithdrawPendingTransfer, "PendingInventoryTransfer", pending.Id.ToString(),
                $"Withdrew approved transfer from {pending.FromWarehouse} to {pending.ToWarehouse} after it failed to post. Reason: {pending.WithdrawalReason}",
                true);
        }
        catch { }

        return new PendingInventoryTransferDecisionResponseDto
        {
            PendingTransferId = pending.Id,
            Status = pending.Status,
            Decision = ApprovalRequestStatuses.Cancelled,
            ApprovalProcessComplete = true,
            Message = "The transfer was withdrawn and will not be posted to SAP."
        };
    }
}
