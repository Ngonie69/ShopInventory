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

/// <summary>
/// Turns a transfer request down: the SAP document is closed, and where this app is the approval
/// gate the rejection is recorded against the approval process as well.
/// </summary>
/// <remarks>
/// This used to record the local rejection and nothing else, which closed nothing: the SAP document
/// stayed open, so the Transfer Requests tab — which lists what SAP still holds open — kept showing
/// a request that had just been closed. For a request raised directly in SAP it was worse than a
/// no-op. There is no approval to reject, so the decision matched only the catch-all Administrator
/// Review stage and a stock controller was refused outright with 403 "not an authorizer" for a
/// document this app is not the gate for.
/// </remarks>
public sealed class CloseTransferRequestHandler(
    ISAPServiceLayerClient sapClient,
    IInventoryTransferApprovalService approvalService,
    ITransferWarehouseAuthorizer warehouseAuthorizer,
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

            // Whoever may convert a request is also the one who may turn it down.
            var scopeCheck = await warehouseAuthorizer.EnsureCanActOnSourceAsync(
                command.UserId, document.FromWarehouse, cancellationToken);
            if (scopeCheck.IsError) return scopeCheck.Errors;

            // A request this app never raised has no approval to reject — closing it in SAP is the
            // whole of the action, and routing a decision at it would only refuse the caller for a
            // stage that exists solely because we asked for one.
            if (!await approvalService.HasApprovalAsync(
                    ApprovalDocumentContext.ForTransferRequest(document), cancellationToken))
            {
                return await CloseWithoutApprovalAsync(command, document, cancellationToken);
            }

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

            // Only a rejection that finishes the process closes the document: a stage that still
            // wants a second refusal has not turned the request down yet.
            var message = decision.Value.Message;
            if (decision.Value.Rejected && !IsClosed(document.DocumentStatus))
            {
                try
                {
                    await sapClient.CloseInventoryTransferRequestAsync(command.DocEntry, cancellationToken);
                }
                catch (Exception ex)
                {
                    // The decision is written and re-sending it records the same decision again, so
                    // a retry gets another go at the close. Reporting success here would leave the
                    // request open in SAP with everyone believing it was closed.
                    logger.LogError(ex, "Rejected transfer request {DocEntry} but could not close it in SAP", command.DocEntry);
                    return Errors.InventoryTransfer.TransferRequestCloseFailed(command.DocEntry, ex.Message, decisionRecorded: true);
                }

                message = $"{decision.Value.Message} Transfer request #{document.DocNum} has been closed in SAP.";
                try
                {
                    await auditService.LogAsync(AuditActions.CloseTransferRequest, "TransferRequest", command.DocEntry.ToString(),
                        $"Closed request #{document.DocNum} in SAP after the approval process rejected it", true);
                }
                catch { }
            }

            var response = new TransferRequestDecisionResponseDto
            {
                Message = message,
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Errors.InventoryTransfer.CreationFailed("Request was canceled by the client");
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

    /// <summary>
    /// Closes a request this app never raised. There are no approval stages to decide, so the
    /// authorisation is the caller's own — the source-warehouse scope check has already had its
    /// say — and the record of it is the audit trail.
    /// </summary>
    private async Task<ErrorOr<TransferRequestDecisionResponseDto>> CloseWithoutApprovalAsync(
        CloseTransferRequestCommand command,
        InventoryTransferRequest document,
        CancellationToken cancellationToken)
    {
        long? idempotencyRequestId = null;
        var release = false;
        try
        {
            var key = $"{command.DocEntry}:direct:{command.UserId}";
            var acquired = await idempotencyRequestStore.TryAcquireAsync<TransferRequestDecisionResponseDto>(
                "inventory-transfer-request-direct-close", key,
                new { command.DocEntry, command.UserId }, cancellationToken);
            switch (acquired.Outcome)
            {
                case IdempotencyAcquireOutcome.ReplayAvailable when acquired.Response is not null: return acquired.Response;
                case IdempotencyAcquireOutcome.InProgress: return Errors.InventoryTransfer.ApprovalInProgress;
                case IdempotencyAcquireOutcome.RequestMismatch: return Errors.Idempotency.RequestMismatch("transfer request closure");
                case IdempotencyAcquireOutcome.Acquired: idempotencyRequestId = acquired.RequestId; release = true; break;
            }

            var alreadyClosed = IsClosed(document.DocumentStatus);
            if (!alreadyClosed)
            {
                try
                {
                    await sapClient.CloseInventoryTransferRequestAsync(command.DocEntry, cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to close transfer request {DocEntry} in SAP", command.DocEntry);
                    return Errors.InventoryTransfer.TransferRequestCloseFailed(command.DocEntry, ex.Message);
                }

                try
                {
                    await auditService.LogAsync(AuditActions.CloseTransferRequest, "TransferRequest", command.DocEntry.ToString(),
                        $"Closed request #{document.DocNum} in SAP without converting it " +
                        $"(raised in SAP, source warehouse {document.FromWarehouse})", true);
                }
                catch { }
            }

            var response = new TransferRequestDecisionResponseDto
            {
                Message = alreadyClosed
                    ? $"Transfer request #{document.DocNum} is already closed in SAP."
                    : $"Transfer request #{document.DocNum} has been closed in SAP.",
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
        finally
        {
            if (release && idempotencyRequestId.HasValue)
            {
                try { await idempotencyRequestStore.ReleaseAsync(idempotencyRequestId.Value, CancellationToken.None); }
                catch (Exception ex) { logger.LogWarning(ex, "Failed to release the direct closure lock"); }
            }
        }
    }

    private static bool IsClosed(string? documentStatus) =>
        string.Equals(documentStatus, "bost_Close", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(documentStatus, "Closed", StringComparison.OrdinalIgnoreCase);
}
