using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Idempotency;
using ShopInventory.Common.Stock;
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
    IStockValidationService stockValidation,
    IUnpostedTillClaims tillClaims,
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

                // Every conversion passes through the request's approval, whoever presses Convert.
                // Administrators, stock officers and the wash bay used to convert straight past it,
                // even while the request still sat awaiting someone else, so the approval gated
                // nothing for most requests. A request raised in SAP has no approval yet; one is
                // opened here, routed by whoever raised it (the audit log or the requester in its
                // comments), never by the person converting it.
                var approval = await approvalService.EnsureRequestAsync(document, null, cancellationToken);
                // Generated as well as Approved: a conversion that posted and then failed to close
                // the request leaves it open in SAP with its approval marked generated. Going through
                // again finds that transfer by its marker and closes the request, posting nothing.
                if (approval.Status is ApprovalRequestStatuses.Approved
                    or ApprovalRequestStatuses.Generated
                    or ApprovalRequestStatuses.GeneratedByAuthorizer)
                    return await GenerateApprovedAsync(command, document, cancellationToken);
                if (approval.Status != ApprovalRequestStatuses.Pending)
                    return Errors.InventoryTransfer.ApprovalAlreadyDecided(approval.Status);

                // Someone who may decide a pending stage records that decision below and, if it is
                // the last one, posts in the same action. Anyone else is told what it is waiting for.
                var context = ApprovalDocumentContext.ForTransferRequest(document);
                if (!await approvalService.CanAuthorizePendingStageAsync(context, command.UserId, command.StageId, cancellationToken))
                {
                    var (_, stages) = await approvalService.GetProgressAsync(context, cancellationToken);
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
                            $"Conversion of request #{document.DocNum} asked for before approval. Awaiting: {awaiting}",
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
                var converted = await ConvertOnceAsync(document, cancellationToken);
                if (converted.IsError)
                    return converted.Errors;

                var (transfer, alreadyInSap) = converted.Value;
                transferDto = transfer.ToDto();
                await approvalService.MarkGeneratedAsync(command.DocEntry, transfer.DocEntry, transfer.DocNum, command.UserId, true, cancellationToken);
                message = alreadyInSap
                    ? AlreadyConvertedMessage(document, transfer)
                    : $"Approval complete and Inventory Transfer #{transfer.DocNum} generated by the authorizer.";
                try
                {
                    await auditService.LogAsync(AuditActions.ConvertTransferRequest, "TransferRequest", command.DocEntry.ToString(),
                        alreadyInSap
                            ? $"Found transfer {transfer.DocEntry} already made from request #{document.DocNum}; closed the request without posting again"
                            : $"Generated transfer {transfer.DocEntry} from approved request", true);
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
    /// The lock that lets one conversion of a request run at a time. Keyed on the request alone:
    /// two people converting the same request must collide, and a key that named the user would let
    /// them both through.
    /// </summary>
    internal const string ConversionPostScope = "transfer-request-conversion-post";

    /// <summary>
    /// Posts the transfer for a request, unless SAP already holds one made from it.
    /// </summary>
    /// <remarks>
    /// <para>The transfer is posted first and the request closed in a second call, and a close that
    /// fails is only logged: the stock has already moved. That left the request open in SAP, where
    /// anyone could convert it again and move the stock a second time. A post whose reply was lost
    /// did the same. Each transfer made here carries
    /// <see cref="InventoryTransferRequestConversion.OrderNumberFor"/> in <c>U_OrderNumber</c>, so this
    /// looks for it first and, when it is there, closes the request instead of posting again.</para>
    ///
    /// <para>The lookup runs before the till-sale stock check on purpose. Once an earlier conversion
    /// has moved the stock, that check reads the depot as short and would refuse, leaving the request
    /// open for good.</para>
    ///
    /// <para>Transfers converted before the marker existed, or converted in the SAP client, carry no
    /// marker and are not found. Those requests were closed by their conversion in almost every
    /// case, and the open-status check in the SAP client still refuses a closed one.</para>
    /// </remarks>
    private async Task<ErrorOr<(InventoryTransfer Transfer, bool AlreadyInSap)>> ConvertOnceAsync(
        InventoryTransferRequest document,
        CancellationToken cancellationToken)
    {
        var acquired = await idempotencyRequestStore.TryAcquireAsync<int>(
            ConversionPostScope,
            document.DocEntry.ToString(),
            new { document.DocEntry },
            cancellationToken);
        switch (acquired.Outcome)
        {
            case IdempotencyAcquireOutcome.InProgress:
                return Errors.InventoryTransfer.ConversionInProgress(document.DocNum);
            case IdempotencyAcquireOutcome.RequestMismatch:
                return Errors.Idempotency.RequestMismatch("transfer request conversion");
        }

        // The lock is a mutex only. It is never completed, because the marker in SAP, not a stored
        // reply, is what says a conversion happened.
        var lockId = acquired.RequestId;
        try
        {
            var orderNumber = InventoryTransferRequestConversion.OrderNumberFor(document.DocEntry);
            InventoryTransfer? earlier;
            try
            {
                earlier = await sapClient.FindStockTransferByOrderNumberAsync(
                    orderNumber,
                    InventoryTransferRequestConversion.LookupFrom(document, DateTime.Today),
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogError(exception,
                    "Could not look for an earlier conversion of transfer request {DocEntry} ({OrderNumber}) in SAP",
                    document.DocEntry, orderNumber);
                return Errors.InventoryTransfer.SapConnectionError(
                    $"Could not check SAP for an earlier conversion of request #{document.DocNum}, so nothing was " +
                    "transferred. Try again in a minute.");
            }

            if (earlier is not null)
            {
                logger.LogWarning(
                    "Transfer request {DocEntry} was already converted to transfer {TransferDocEntry} ({OrderNumber}); " +
                    "closing the request instead of posting again",
                    document.DocEntry, earlier.DocEntry, orderNumber);
                await CloseQuietlyAsync(document.DocEntry);
                return (earlier, true);
            }

            if (await RefuseIfTillSalesWouldBeTakenAsync(document, cancellationToken) is { } refusal)
                return refusal;

            var transfer = await sapClient.ConvertTransferRequestToTransferAsync(document.DocEntry, cancellationToken);
            return (transfer, false);
        }
        finally
        {
            if (lockId is { } id)
            {
                try { await idempotencyRequestStore.ReleaseAsync(id, CancellationToken.None); }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Failed to release the conversion lock for transfer request {DocEntry}", document.DocEntry);
                }
            }
        }
    }

    /// <summary>
    /// Closes a request whose transfer is already in SAP. A failure is logged, not raised: the stock
    /// has moved, and the next conversion finds the transfer by its marker and tries the close again.
    /// </summary>
    private async Task CloseQuietlyAsync(int requestDocEntry)
    {
        try
        {
            await sapClient.CloseInventoryTransferRequestAsync(requestDocEntry, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "Transfer request {DocEntry} could not be closed in SAP; its transfer is already posted", requestDocEntry);
        }
    }

    private static string AlreadyConvertedMessage(InventoryTransferRequest document, InventoryTransfer transfer) =>
        $"Request #{document.DocNum} had already been converted to Inventory Transfer #{transfer.DocNum}. " +
        "Nothing new was posted, and the request has been closed.";

    /// <summary>
    /// Refuses the conversion when the transfer would take units a till has sold out of the source
    /// warehouse and SAP has not yet invoiced. Null when it may go ahead.
    /// </summary>
    /// <remarks>
    /// <para>A van's stock request is how most stock leaves a depot, and converting one posts straight
    /// to SAP: nothing else checks it first. KEFGRC is both a depot the Harare vans load from and a
    /// vending depot, and on 2026-09-28 transfer 89450 to VAN002 took all 685 YOG145 SAP held, 8 of
    /// them already sold on a vending receipt three minutes earlier. That sale could then not be
    /// invoiced. See <see cref="IUnpostedTillClaims"/>.</para>
    ///
    /// <para>The till-sale check is asked first because it is a local read, and only when it names one
    /// of the requested items is SAP's stock read — so a conversion from a warehouse no till sells from,
    /// which is most of them, costs nothing more than it did.</para>
    ///
    /// <para>A stock read that fails refuses rather than posts. Converting anyway is exactly the case
    /// this exists for, and a till sale normally reaches SAP within a minute, so waiting is cheap.</para>
    /// </remarks>
    private async Task<Error?> RefuseIfTillSalesWouldBeTakenAsync(
        InventoryTransferRequest document,
        CancellationToken cancellationToken)
    {
        var transfer = InventoryTransferRequestConversion.ToTransfer(document);
        var lines = transfer.Lines ?? [];

        var anyHeld = false;
        foreach (var source in lines.GroupBy(line => line.FromWarehouseCode!, StringComparer.OrdinalIgnoreCase))
        {
            var claims = await tillClaims.ForWarehouseAsync(source.Key, cancellationToken);
            if (source.Any(line => line.ItemCode is { } itemCode && claims.ContainsKey(itemCode)))
            {
                anyHeld = true;
                break;
            }
        }

        if (!anyHeld)
            return null;

        var validation = await stockValidation.ValidateInventoryTransferStockAsync(transfer, cancellationToken);

        if (!validation.StockWasFullyRead)
        {
            return Errors.InventoryTransfer.SapConnectionError(
                $"Could not read stock from SAP for {string.Join(", ", validation.UnreadableWarehouses)}, so request "
                + $"#{document.DocNum} cannot be checked against sales the tills have made that SAP does not have yet. "
                + "Nothing was transferred; try again in a minute.");
        }

        if (validation.IsValid)
            return null;

        logger.LogWarning(
            "Refused to convert transfer request {DocNum} from {Warehouse}: {Shortages}",
            document.DocNum, document.FromWarehouse, string.Join("; ", validation.Errors.Select(error => error.Message)));

        return Errors.InventoryTransfer.InsufficientStock(
            $"Request #{document.DocNum} was not converted, and nothing was transferred. "
            + string.Join("; ", validation.Errors.Select(error => error.Message))
            + ". Till sales normally reach SAP within a minute, so try again shortly, or reduce the request.");
    }

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

    /// <summary>
    /// Posts the transfer for a request whose approval is already complete. Nobody decides anything
    /// here, so the person generating it may be the one who raised it: the approval was someone
    /// else's, and this only carries it out.
    /// </summary>
    private async Task<ErrorOr<TransferRequestConvertedResponseDto>> GenerateApprovedAsync(
        ConvertTransferRequestCommand command,
        InventoryTransferRequest document,
        CancellationToken cancellationToken)
    {
        long? idempotencyRequestId = null;
        var release = false;
        try
        {
            var key = $"{command.DocEntry}:approved:{command.UserId}";
            var acquired = await idempotencyRequestStore.TryAcquireAsync<TransferRequestConvertedResponseDto>(
                "inventory-transfer-request-generate-approved", key,
                new { command.DocEntry, command.UserId }, cancellationToken);
            switch (acquired.Outcome)
            {
                case IdempotencyAcquireOutcome.ReplayAvailable when acquired.Response is not null: return acquired.Response;
                case IdempotencyAcquireOutcome.InProgress: return Errors.InventoryTransfer.ApprovalInProgress;
                case IdempotencyAcquireOutcome.RequestMismatch: return Errors.Idempotency.RequestMismatch("transfer request conversion");
                case IdempotencyAcquireOutcome.Acquired: idempotencyRequestId = acquired.RequestId; release = true; break;
            }

            var converted = await ConvertOnceAsync(document, cancellationToken);
            if (converted.IsError)
                return converted.Errors;

            var (transfer, alreadyInSap) = converted.Value;
            await approvalService.MarkGeneratedAsync(
                command.DocEntry, transfer.DocEntry, transfer.DocNum, command.UserId, false, cancellationToken);

            try
            {
                await auditService.LogAsync(AuditActions.ConvertTransferRequest, "TransferRequest", command.DocEntry.ToString(),
                    alreadyInSap
                        ? $"Found transfer {transfer.DocEntry} already made from request #{document.DocNum}; closed the request without posting again"
                        : $"Generated transfer {transfer.DocEntry} from approved request #{document.DocNum}", true);
            }
            catch { }

            await NotifyRequesterAsync(document, transfer, cancellationToken);

            var response = new TransferRequestConvertedResponseDto
            {
                Message = alreadyInSap
                    ? AlreadyConvertedMessage(document, transfer)
                    : $"Inventory Transfer #{transfer.DocNum} generated from request #{document.DocNum}.",
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
                catch (Exception ex) { logger.LogWarning(ex, "Failed to release the approved-request generation lock"); }
            }
        }
    }
}
