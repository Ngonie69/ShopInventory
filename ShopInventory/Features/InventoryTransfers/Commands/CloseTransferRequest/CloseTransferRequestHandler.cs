using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Idempotency;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.InventoryTransfers.Commands.CloseTransferRequest;

public sealed class CloseTransferRequestHandler(
    ISAPServiceLayerClient sapClient,
    IInventoryTransferApprovalService approvalService,
    IIdempotencyRequestStore idempotencyRequestStore,
    IAuditService auditService,
    IOptions<SAPSettings> settings,
    ILogger<CloseTransferRequestHandler> logger)
    : IRequestHandler<CloseTransferRequestCommand, ErrorOr<TransferRequestDecisionResponseDto>>
{
    public async Task<ErrorOr<TransferRequestDecisionResponseDto>> Handle(CloseTransferRequestCommand command, CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled) return Errors.InventoryTransfer.SapDisabled;
        long? idempotencyRequestId = null;
        var release = false;
        try
        {
            var document = await sapClient.GetInventoryTransferRequestByDocEntryAsync(command.DocEntry, cancellationToken);
            if (document is null) return Errors.InventoryTransfer.TransferRequestNotFound(command.DocEntry);

            var key = $"{command.DocEntry}:{command.StageId?.ToString() ?? "auto"}:{command.UserId}:reject";
            var acquired = await idempotencyRequestStore.TryAcquireAsync<TransferRequestDecisionResponseDto>(
                "inventory-transfer-approval-decision", key,
                new { command.DocEntry, command.StageId, command.UserId, Decision = ApprovalDecisionValues.NotApproved }, cancellationToken);
            switch (acquired.Outcome)
            {
                case IdempotencyAcquireOutcome.ReplayAvailable when acquired.Response is not null: return acquired.Response;
                case IdempotencyAcquireOutcome.InProgress: return Errors.InventoryTransfer.ApprovalInProgress;
                case IdempotencyAcquireOutcome.RequestMismatch: return Errors.Idempotency.RequestMismatch("transfer rejection decision");
                case IdempotencyAcquireOutcome.Acquired: idempotencyRequestId = acquired.RequestId; release = true; break;
            }

            var decision = await approvalService.SubmitDecisionAsync(
                document, command.UserId, ApprovalDecisionValues.NotApproved, command.StageId, command.Remarks, cancellationToken);
            if (decision.IsError) return decision.Errors;
            try
            {
                await auditService.LogAsync(AuditActions.RejectTransferRequestStage, "TransferRequest", command.DocEntry.ToString(),
                    $"Rejected stage '{decision.Value.StageName}'. Status: {decision.Value.RequestStatus}. Remarks: {command.Remarks}", true);
            }
            catch { }

            var response = new TransferRequestDecisionResponseDto
            {
                Message = decision.Value.Message,
                RequestDocEntry = command.DocEntry,
                Decision = ApprovalDecisionValues.NotApproved
            };
            if (idempotencyRequestId.HasValue)
            {
                await idempotencyRequestStore.CompleteAsync(idempotencyRequestId.Value, response, cancellationToken);
                release = false;
            }
            return response;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error rejecting transfer request {DocEntry}", command.DocEntry);
            return Errors.InventoryTransfer.CreationFailed(ex.Message);
        }
        finally
        {
            if (release && idempotencyRequestId.HasValue)
            {
                try { await idempotencyRequestStore.ReleaseAsync(idempotencyRequestId.Value, CancellationToken.None); }
                catch (Exception ex) { logger.LogWarning(ex, "Failed to release transfer rejection decision lock"); }
            }
        }
    }
}
