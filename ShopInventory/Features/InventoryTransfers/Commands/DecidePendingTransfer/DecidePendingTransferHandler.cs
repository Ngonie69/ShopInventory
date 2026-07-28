using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Idempotency;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.InventoryTransfers.Commands.DecidePendingTransfer;

public sealed class DecidePendingTransferHandler(
    ApplicationDbContext context,
    IInventoryTransferApprovalService approvalService,
    ITransferWarehouseAuthorizer warehouseAuthorizer,
    IPendingInventoryTransferPoster poster,
    IIdempotencyRequestStore idempotencyRequestStore,
    IAuditService auditService,
    IOptions<SAPSettings> settings,
    ILogger<DecidePendingTransferHandler> logger)
    : IRequestHandler<DecidePendingTransferCommand, ErrorOr<PendingInventoryTransferDecisionResponseDto>>
{
    public async Task<ErrorOr<PendingInventoryTransferDecisionResponseDto>> Handle(
        DecidePendingTransferCommand command,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled) return Errors.InventoryTransfer.SapDisabled;

        var approving = string.Equals(command.Decision, ApprovalDecisionValues.Approved, StringComparison.OrdinalIgnoreCase);
        var rejecting = string.Equals(command.Decision, ApprovalDecisionValues.NotApproved, StringComparison.OrdinalIgnoreCase);
        if (!approving && !rejecting)
            return Errors.InventoryTransfer.ValidationFailed("Decision must be Approved or NotApproved.");

        var pending = await context.PendingInventoryTransfers
            .FirstOrDefaultAsync(item => item.Id == command.PendingTransferId, cancellationToken);
        if (pending is null)
            return Errors.InventoryTransfer.PendingTransferNotFound(command.PendingTransferId);

        if (pending.Status != PendingInventoryTransferStatuses.AwaitingApproval)
            return Errors.InventoryTransfer.PendingTransferNotActionable(pending.Status);

        var scopeCheck = await warehouseAuthorizer.EnsureCanActOnSourceAsync(
            command.UserId, pending.FromWarehouse, cancellationToken);
        if (scopeCheck.IsError)
            return scopeCheck.Errors;

        long? idempotencyRequestId = null;
        var release = false;
        try
        {
            var key = $"{pending.Id}:{command.StageId?.ToString() ?? "auto"}:{command.UserId}:{command.Decision}";
            var acquired = await idempotencyRequestStore.TryAcquireAsync<PendingInventoryTransferDecisionResponseDto>(
                "pending-inventory-transfer-decision", key,
                new { command.PendingTransferId, command.StageId, command.UserId, command.Decision }, cancellationToken);
            switch (acquired.Outcome)
            {
                case IdempotencyAcquireOutcome.ReplayAvailable when acquired.Response is not null: return acquired.Response;
                case IdempotencyAcquireOutcome.InProgress: return Errors.InventoryTransfer.ApprovalInProgress;
                case IdempotencyAcquireOutcome.RequestMismatch: return Errors.Idempotency.RequestMismatch("inventory transfer approval decision");
                case IdempotencyAcquireOutcome.Acquired: idempotencyRequestId = acquired.RequestId; release = true; break;
            }

            var decision = await approvalService.SubmitDecisionAsync(
                ApprovalDocumentContext.ForPendingTransfer(pending),
                command.UserId,
                approving ? ApprovalDecisionValues.Approved : ApprovalDecisionValues.NotApproved,
                command.StageId,
                command.Remarks,
                cancellationToken);
            if (decision.IsError) return decision.Errors;

            try
            {
                await auditService.LogAsync(
                    approving ? AuditActions.ApproveTransferStage : AuditActions.RejectTransferStage,
                    "PendingInventoryTransfer", pending.Id.ToString(),
                    $"{command.Decision} stage '{decision.Value.StageName}'. Status: {decision.Value.RequestStatus}. Remarks: {command.Remarks}",
                    true);
            }
            catch { }

            var response = new PendingInventoryTransferDecisionResponseDto
            {
                PendingTransferId = pending.Id,
                Decision = approving ? ApprovalDecisionValues.Approved : ApprovalDecisionValues.NotApproved,
                ApprovalProcessComplete = decision.Value.ApprovalProcessComplete,
                Message = decision.Value.Message,
                Status = pending.Status
            };

            if (decision.Value.Rejected)
            {
                pending.Status = PendingInventoryTransferStatuses.Rejected;
                pending.DecidedAtUtc = DateTime.UtcNow;
                await context.SaveChangesAsync(cancellationToken);
                response.Status = pending.Status;
                response.Message = "The inventory transfer was rejected and will not be posted to SAP.";
            }
            else if (decision.Value.ApprovalProcessComplete)
            {
                pending.Status = PendingInventoryTransferStatuses.Approved;
                pending.DecidedAtUtc = DateTime.UtcNow;
                await context.SaveChangesAsync(cancellationToken);

                var posted = await poster.PostAsync(pending, command.UserId, cancellationToken);
                if (posted.IsError)
                {
                    // The approval itself stands; only the SAP post failed, so surface the error
                    // without replaying the decision. The transfer can be posted again later.
                    return posted.Errors;
                }

                response.Transfer = posted.Value;
                response.Status = pending.Status;
                response.Message = $"Approval complete. Inventory transfer #{posted.Value.DocNum} posted to SAP.";
            }

            if (idempotencyRequestId.HasValue)
            {
                await idempotencyRequestStore.CompleteAsync(idempotencyRequestId.Value, response, cancellationToken);
                release = false;
            }

            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Errors.InventoryTransfer.CreationFailed("Request was canceled by the client");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Error recording an approval decision for pending inventory transfer {PendingId}", command.PendingTransferId);
            return Errors.InventoryTransfer.CreationFailed(exception.Message);
        }
        finally
        {
            if (release && idempotencyRequestId.HasValue)
            {
                try { await idempotencyRequestStore.ReleaseAsync(idempotencyRequestId.Value, CancellationToken.None); }
                catch (Exception exception) { logger.LogWarning(exception, "Failed to release the pending transfer decision lock"); }
            }
        }
    }
}
