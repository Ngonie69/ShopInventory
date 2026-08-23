using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Idempotency;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Mappings;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.InventoryTransfers.Commands.ConvertTransferRequest;

public sealed class ConvertTransferRequestHandler(
    ISAPServiceLayerClient sapClient,
    IInventoryTransferApprovalService approvalService,
    ITransferWarehouseAuthorizer warehouseAuthorizer,
    IIdempotencyRequestStore idempotencyRequestStore,
    IAuditService auditService,
    INotificationService notificationService,
    IOptions<SAPSettings> settings,
    ILogger<ConvertTransferRequestHandler> logger)
    : IRequestHandler<ConvertTransferRequestCommand, ErrorOr<TransferRequestConvertedResponseDto>>
{
    public async Task<ErrorOr<TransferRequestConvertedResponseDto>> Handle(ConvertTransferRequestCommand command, CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled) return Errors.InventoryTransfer.SapDisabled;
        long? idempotencyRequestId = null;
        var release = false;
        try
        {
            var document = await sapClient.GetInventoryTransferRequestByDocEntryAsync(command.DocEntry, cancellationToken);
            if (document is null) return Errors.InventoryTransfer.TransferRequestNotFound(command.DocEntry);

            if (command.GenerateDocument)
            {
                var conversionCheck = await warehouseAuthorizer.EnsureCanConvertRequestAsync(
                    command.UserId, document.FromWarehouse, cancellationToken);
                if (conversionCheck.IsError)
                    return conversionCheck.Errors;

                // Admins and stock controllers may convert any request directly. A depot
                // controller reaches this point only for an assigned source warehouse, but must
                // still pass through an approval stage before anything posts to SAP.
                var sourceScope = await warehouseAuthorizer.GetSourceScopeAsync(
                    command.UserId, cancellationToken);
                // A stage id means the caller used the approval action (including "Approve &
                // Add"). Record that decision before generating instead of taking the role's
                // ordinary direct-conversion shortcut.
                if (sourceScope is null && !command.StageId.HasValue)
                    return await ConvertWithoutApprovalAsync(command, document, cancellationToken);

                if (sourceScope is not null)
                {
                    var approval = await approvalService.EnsureRequestAsync(
                        document, command.UserId, cancellationToken);
                    var (_, stages) = await approvalService.GetProgressAsync(
                        ApprovalDocumentContext.ForTransferRequest(document), cancellationToken);
                    var depotMayApprovePendingStage = stages.Any(stage =>
                        string.Equals(stage.Status, ApprovalRequestStatuses.Pending, StringComparison.OrdinalIgnoreCase) &&
                        (stage.AuthorizerUserIds.Contains(command.UserId) ||
                         stage.AuthorizerRoles.Contains(ApplicationRoles.DepotController, StringComparer.OrdinalIgnoreCase)));

                    if (!depotMayApprovePendingStage)
                    {
                        var pendingStages = stages
                            .Where(stage => string.Equals(
                                stage.Status, ApprovalRequestStatuses.Pending, StringComparison.OrdinalIgnoreCase))
                            .Select(stage => stage.StageName)
                            .ToList();
                        var awaiting = pendingStages.Count == 0
                            ? approval.TemplateName
                            : string.Join(", ", pendingStages);

                        try
                        {
                            await auditService.LogAsync(
                                AuditActions.SubmitTransferForApproval,
                                "TransferRequest",
                                command.DocEntry.ToString(),
                                $"Depot controller submitted conversion of request #{document.DocNum} for approval. Awaiting: {awaiting}",
                                true);
                        }
                        catch { }

                        return new TransferRequestConvertedResponseDto
                        {
                            Message = $"Transfer request #{document.DocNum} is awaiting {awaiting} before it can post to SAP.",
                            RequestDocEntry = command.DocEntry,
                            Transfer = null
                        };
                    }
                }
            }

            var key = $"{command.DocEntry}:{command.StageId?.ToString() ?? "auto"}:{command.UserId}:approve:{command.GenerateDocument}";
            var acquired = await idempotencyRequestStore.TryAcquireAsync<TransferRequestConvertedResponseDto>(
                "inventory-transfer-approval-decision", key,
                new { command.DocEntry, command.StageId, command.UserId, command.GenerateDocument, Decision = ApprovalDecisionValues.Approved }, cancellationToken);
            switch (acquired.Outcome)
            {
                case IdempotencyAcquireOutcome.ReplayAvailable when acquired.Response is not null: return acquired.Response;
                case IdempotencyAcquireOutcome.InProgress: return Errors.InventoryTransfer.ApprovalInProgress;
                case IdempotencyAcquireOutcome.RequestMismatch: return Errors.Idempotency.RequestMismatch("transfer approval decision");
                case IdempotencyAcquireOutcome.Acquired: idempotencyRequestId = acquired.RequestId; release = true; break;
            }

            var decision = await approvalService.SubmitDecisionAsync(
                document, command.UserId, ApprovalDecisionValues.Approved, command.StageId, command.Remarks, cancellationToken);
            if (decision.IsError) return decision.Errors;

            try
            {
                await auditService.LogAsync(AuditActions.ApproveTransferRequestStage, "TransferRequest", command.DocEntry.ToString(),
                    $"Approved stage '{decision.Value.StageName}'. Status: {decision.Value.RequestStatus}", true);
            }
            catch { }

            InventoryTransferDto? transferDto = null;
            var message = decision.Value.Message;
            if (decision.Value.ApprovalProcessComplete && command.GenerateDocument)
            {
                var transfer = await sapClient.ConvertTransferRequestToTransferAsync(command.DocEntry, cancellationToken);
                transferDto = transfer.ToDto();
                await approvalService.MarkGeneratedAsync(command.DocEntry, transfer.DocEntry, transfer.DocNum, command.UserId, true, cancellationToken);
                message = $"Approval complete and Inventory Transfer #{transfer.DocNum} generated by the authorizer.";
                try
                {
                    await auditService.LogAsync(AuditActions.ConvertTransferRequest, "TransferRequest", command.DocEntry.ToString(),
                        $"Generated transfer {transfer.DocEntry} from approved request", true);
                }
                catch { }

                await NotifyRequesterAsync(document, transfer, cancellationToken);
            }

            var response = new TransferRequestConvertedResponseDto
            {
                Message = message,
                RequestDocEntry = command.DocEntry,
                Transfer = transferDto
            };
            if (idempotencyRequestId.HasValue)
            {
                await idempotencyRequestStore.CompleteAsync(idempotencyRequestId.Value, response, cancellationToken);
                release = false;
            }
            return response;
        }
        catch (InvalidOperationException ex)
        {
            return Errors.InventoryTransfer.InvalidOperation(ex.Message);
        }

        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Errors.InventoryTransfer.CreationFailed("Request was canceled by the client");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing approval for transfer request {DocEntry}", command.DocEntry);
            return Errors.InventoryTransfer.CreationFailed(ex.Message);
        }
        finally
        {
            if (release && idempotencyRequestId.HasValue)
            {
                try { await idempotencyRequestStore.ReleaseAsync(idempotencyRequestId.Value, CancellationToken.None); }
                catch (Exception ex) { logger.LogWarning(ex, "Failed to release transfer approval decision lock"); }
            }
        }
    }

    /// <summary>
    /// Converts a request the caller is authorised to issue stock for. The approval request, where
    /// there is one, is closed off as generated so the document history remains accurate even
    /// though conversion does not depend on where the request originated.
    /// </summary>
    /// <summary>
    /// Tells the rep who asked for this stock that it is now moving.
    /// </summary>
    /// <remarks>
    /// <para>The one event on a van's day that the handset cannot find out about on its own. A request
    /// and the transfer that answers it are two documents on two different routes, and SAP holds the
    /// link between them on the transfer line as <c>BaseEntry</c> — which neither the <c>$select</c>
    /// nor the DTO carries. All the handset can see is the sentence the conversion writes into the
    /// transfer's remarks, which it parses. Reword that sentence and the link goes quiet: every request
    /// reads as outstanding while its stock is on the van, and nothing on the handset says otherwise.
    /// This notification carries the pair as fields, so the one moment that matters does not depend on
    /// the wording holding.</para>
    ///
    /// <para>Addressed to the originator rather than broadcast, and that is the difference between it
    /// arriving and not. A broadcast on category "InventoryTransfer" resolves through
    /// <c>NotificationAudienceRules.InventoryAudienceRoles</c> to Admin, StockController and
    /// DepotController — the van roles that actually drive routes, ADR and Sales, are in none of them.
    /// A targeted notification skips the audience rules and goes to that user's devices.</para>
    ///
    /// <para>Never allowed to fail the conversion. The transfer is posted in SAP and the stock is
    /// moving by the time this runs; throwing here would fail a request that already succeeded, and
    /// the caller would reasonably retry it.</para>
    /// </remarks>
    private async Task NotifyRequesterAsync(
        InventoryTransferRequest document,
        InventoryTransfer transfer,
        CancellationToken cancellationToken)
    {
        try
        {
            var context = ApprovalDocumentContext.ForTransferRequest(document);

            // Asked before reading, because the read is not one. EnsureRequestAsync creates the
            // record when there is none, and a request raised straight into SAP deliberately has
            // none — listing stopped opening approvals against those, and converting one must not
            // put the stub rows back. Notifying is not worth undoing that.
            if (!await approvalService.HasApprovalAsync(context, cancellationToken))
            {
                logger.LogInformation(
                    "Transfer request #{DocNum} converted to transfer #{TransferDocNum}; it was raised in SAP, " +
                    "so there is no requester to notify", document.DocNum, transfer.DocNum);
                return;
            }

            // Null originator rather than the converting user: this reads an existing record, and
            // passing a user here would stamp whoever is converting as the person who asked.
            var approval = await approvalService.EnsureRequestAsync(context, null, cancellationToken);

            if (approval.OriginatorUserId is not { } originator)
            {
                logger.LogInformation(
                    "Transfer request #{DocNum} converted to transfer #{TransferDocNum}, but no originator is on " +
                    "file so no one was notified", document.DocNum, transfer.DocNum);
                return;
            }

            var fromWarehouse = document.FromWarehouse ?? "the depot";
            var toWarehouse = document.ToWarehouse ?? "your warehouse";

            await notificationService.CreateNotificationAsync(
                new CreateNotificationRequest
                {
                    Title = $"Stock request #{document.DocNum} is on its way",
                    Message =
                        $"Transfer #{transfer.DocNum} was raised against it, from {fromWarehouse} to {toWarehouse}.",
                    Type = "Success",
                    Category = "InventoryTransfer",

                    // The transfer, not the request. What the rep does next is check the stock against
                    // the van, and the transfer is the document carrying the lines that actually moved
                    // — a request only ever says what was asked for. DocEntry because that is the
                    // identity SAP and the handset agree on; a van's warehouse is loaded by more than
                    // one depot and duplicate DocNums across them are ordinary.
                    EntityType = "InventoryTransfer",
                    EntityId = transfer.DocEntry.ToString(),
                    ActionUrl = "/inventory-transfers",
                    TargetUserId = originator,
                    Data = new Dictionary<string, string>
                    {
                        ["transferDocEntry"] = transfer.DocEntry.ToString(),
                        ["transferDocNum"] = transfer.DocNum.ToString(),
                        ["requestDocEntry"] = document.DocEntry.ToString(),
                        ["requestDocNum"] = document.DocNum.ToString(),
                        ["fromWarehouse"] = fromWarehouse,
                        ["toWarehouse"] = toWarehouse
                    }
                },
                // Past the commit point: the stock is moving whether or not the caller is still here.
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to notify the requester that transfer request {DocEntry} became transfer {TransferDocEntry}",
                document.DocEntry, transfer.DocEntry);
        }
    }

    private async Task<ErrorOr<TransferRequestConvertedResponseDto>> ConvertWithoutApprovalAsync(
        ConvertTransferRequestCommand command,
        InventoryTransferRequest document,
        CancellationToken cancellationToken)
    {
        long? idempotencyRequestId = null;
        var release = false;
        try
        {
            var key = $"{command.DocEntry}:direct:{command.UserId}";
            var acquired = await idempotencyRequestStore.TryAcquireAsync<TransferRequestConvertedResponseDto>(
                "inventory-transfer-request-direct-convert", key,
                new { command.DocEntry, command.UserId }, cancellationToken);
            switch (acquired.Outcome)
            {
                case IdempotencyAcquireOutcome.ReplayAvailable when acquired.Response is not null: return acquired.Response;
                case IdempotencyAcquireOutcome.InProgress: return Errors.InventoryTransfer.ApprovalInProgress;
                case IdempotencyAcquireOutcome.RequestMismatch: return Errors.Idempotency.RequestMismatch("transfer request conversion");
                case IdempotencyAcquireOutcome.Acquired: idempotencyRequestId = acquired.RequestId; release = true; break;
            }

            var transfer = await sapClient.ConvertTransferRequestToTransferAsync(command.DocEntry, cancellationToken);
            await approvalService.MarkGeneratedAsync(
                command.DocEntry, transfer.DocEntry, transfer.DocNum, command.UserId, true, cancellationToken);

            try
            {
                await auditService.LogAsync(AuditActions.ConvertTransferRequest, "TransferRequest", command.DocEntry.ToString(),
                    $"Generated transfer {transfer.DocEntry} directly from request #{document.DocNum} " +
                    $"(the user is authorised to issue stock from {document.FromWarehouse})", true);
            }
            catch { }

            await NotifyRequesterAsync(document, transfer, cancellationToken);

            var response = new TransferRequestConvertedResponseDto
            {
                Message = $"Inventory Transfer #{transfer.DocNum} generated from request #{document.DocNum}.",
                RequestDocEntry = command.DocEntry,
                Transfer = transfer.ToDto()
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
                catch (Exception ex) { logger.LogWarning(ex, "Failed to release the direct conversion lock"); }
            }
        }
    }
}
