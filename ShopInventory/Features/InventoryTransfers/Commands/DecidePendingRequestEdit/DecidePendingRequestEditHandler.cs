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

namespace ShopInventory.Features.InventoryTransfers.Commands.DecidePendingRequestEdit;

/// <summary>
/// Records an approval decision on a held change to a transfer request, and writes the change
/// to SAP once every stage has approved.
/// </summary>
public sealed class DecidePendingRequestEditHandler(
    ApplicationDbContext context,
    IInventoryTransferApprovalService approvalService,
    IPendingTransferRequestEditApplier applier,
    IIdempotencyRequestStore idempotencyRequestStore,
    IAuditService auditService,
    IOptions<SAPSettings> settings,
    ILogger<DecidePendingRequestEditHandler> logger)
    : IRequestHandler<DecidePendingRequestEditCommand, ErrorOr<PendingTransferRequestEditDecisionResponseDto>>
{
    public async Task<ErrorOr<PendingTransferRequestEditDecisionResponseDto>> Handle(
        DecidePendingRequestEditCommand command,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled) return Errors.InventoryTransfer.SapDisabled;

        var approving = string.Equals(command.Decision, ApprovalDecisionValues.Approved, StringComparison.OrdinalIgnoreCase);
        var rejecting = string.Equals(command.Decision, ApprovalDecisionValues.NotApproved, StringComparison.OrdinalIgnoreCase);
        if (!approving && !rejecting)
            return Errors.InventoryTransfer.ValidationFailed("Decision must be Approved or NotApproved.");

        // Tracked: the decision and the applier both record their outcome on this record.
        var edit = await context.PendingTransferRequestEdits
            .AsTracking()
            .FirstOrDefaultAsync(item => item.Id == command.PendingEditId, cancellationToken);
        if (edit is null)
            return Errors.InventoryTransfer.PendingEditNotFound(command.PendingEditId);

        if (edit.Status != PendingTransferRequestEditStatuses.AwaitingApproval)
            return Errors.InventoryTransfer.PendingEditNotActionable(edit.Status);

        // The person who proposed the change is exactly the person who was not allowed to make
        // it, so they cannot sign it off either.
        if (edit.CreatedByUserId == command.UserId)
            return Errors.InventoryTransfer.SelfApprovalNotAllowed;

        long? idempotencyRequestId = null;
        var release = false;
        try
        {
            var key = $"{edit.Id}:{command.StageId?.ToString() ?? "auto"}:{command.UserId}:{command.Decision}";
            var acquired = await idempotencyRequestStore.TryAcquireAsync<PendingTransferRequestEditDecisionResponseDto>(
                "pending-transfer-request-edit-decision", key,
                new { command.PendingEditId, command.StageId, command.UserId, command.Decision }, cancellationToken);
            switch (acquired.Outcome)
            {
                case IdempotencyAcquireOutcome.ReplayAvailable when acquired.Response is not null: return acquired.Response;
                case IdempotencyAcquireOutcome.InProgress: return Errors.InventoryTransfer.ApprovalInProgress;
                case IdempotencyAcquireOutcome.RequestMismatch: return Errors.Idempotency.RequestMismatch("transfer request change decision");
                case IdempotencyAcquireOutcome.Acquired: idempotencyRequestId = acquired.RequestId; release = true; break;
            }

            var decision = await approvalService.SubmitDecisionAsync(
                ApprovalDocumentContext.ForRequestEdit(edit),
                command.UserId,
                approving ? ApprovalDecisionValues.Approved : ApprovalDecisionValues.NotApproved,
                command.StageId,
                command.Remarks,
                cancellationToken);
            if (decision.IsError) return decision.Errors;

            try
            {
                await auditService.LogAsync(
                    approving ? AuditActions.ApproveTransferRequestEditStage : AuditActions.RejectTransferRequestEditStage,
                    "TransferRequestEdit", edit.Id.ToString(),
                    $"{command.Decision} stage '{decision.Value.StageName}'. Status: {decision.Value.RequestStatus}. Remarks: {command.Remarks}",
                    true);
            }
            catch { }

            var response = new PendingTransferRequestEditDecisionResponseDto
            {
                PendingEditId = edit.Id,
                Decision = approving ? ApprovalDecisionValues.Approved : ApprovalDecisionValues.NotApproved,
                ApprovalProcessComplete = decision.Value.ApprovalProcessComplete,
                Message = decision.Value.Message,
                Status = edit.Status
            };

            if (decision.Value.Rejected)
            {
                edit.Status = PendingTransferRequestEditStatuses.Rejected;
                edit.DecidedAtUtc = DateTime.UtcNow;
                await context.SaveChangesAsync(cancellationToken);
                response.Status = edit.Status;
                response.Message = "The change was rejected and the transfer request is unchanged.";
            }
            else if (decision.Value.ApprovalProcessComplete)
            {
                edit.DecidedAtUtc = DateTime.UtcNow;
                await context.SaveChangesAsync(cancellationToken);

                var applied = await applier.ApplyAsync(edit, command.UserId, cancellationToken);
                if (applied.IsError)
                {
                    // The approval stands; only the SAP write failed. Surface that without
                    // replaying the decision so it can be retried.
                    return applied.Errors;
                }

                await approvalService.MarkGeneratedAsync(
                    ApprovalDocumentTypes.InventoryTransferRequestEdit, edit.Id.ToString(),
                    edit.RequestDocEntry, edit.RequestDocNum, command.UserId, byAuthorizer: true, cancellationToken);

                response.TransferRequest = applied.Value;
                response.Status = edit.Status;
                response.Message = $"Approval complete. Transfer request #{edit.RequestDocNum} updated in SAP.";
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
            logger.LogError(exception, "Error recording a decision for transfer request change {EditId}", command.PendingEditId);
            return Errors.InventoryTransfer.CreationFailed(exception.Message);
        }
        finally
        {
            if (release && idempotencyRequestId.HasValue)
            {
                try { await idempotencyRequestStore.ReleaseAsync(idempotencyRequestId.Value, CancellationToken.None); }
                catch (Exception exception) { logger.LogWarning(exception, "Failed to release the change decision lock"); }
            }
        }
    }
}

/// <summary>
/// Withdraws a change the caller proposed, before any decision has been recorded.
/// </summary>
public sealed class CancelPendingRequestEditHandler(
    ApplicationDbContext context,
    IAuditService auditService)
    : IRequestHandler<CancelPendingRequestEditCommand, ErrorOr<PendingTransferRequestEditDecisionResponseDto>>
{
    public async Task<ErrorOr<PendingTransferRequestEditDecisionResponseDto>> Handle(
        CancelPendingRequestEditCommand command,
        CancellationToken cancellationToken)
    {
        var edit = await context.PendingTransferRequestEdits
            .AsTracking()
            .FirstOrDefaultAsync(item => item.Id == command.PendingEditId, cancellationToken);
        if (edit is null)
            return Errors.InventoryTransfer.PendingEditNotFound(command.PendingEditId);

        if (edit.CreatedByUserId != command.UserId)
            return Error.Forbidden("InventoryTransfer.NotEditOriginator", "Only the person who proposed a change may withdraw it.");

        if (edit.Status != PendingTransferRequestEditStatuses.AwaitingApproval)
            return Errors.InventoryTransfer.PendingEditNotActionable(edit.Status);

        edit.Status = PendingTransferRequestEditStatuses.Cancelled;
        edit.DecidedAtUtc = DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken);

        try
        {
            await auditService.LogAsync(
                AuditActions.CancelPendingTransfer, "TransferRequestEdit", edit.Id.ToString(),
                $"Change to transfer request #{edit.RequestDocNum} withdrawn by the proposer", true);
        }
        catch { }

        return new PendingTransferRequestEditDecisionResponseDto
        {
            PendingEditId = edit.Id,
            Decision = ApprovalRequestStatuses.Cancelled,
            Status = edit.Status,
            Message = "The change was withdrawn."
        };
    }
}
