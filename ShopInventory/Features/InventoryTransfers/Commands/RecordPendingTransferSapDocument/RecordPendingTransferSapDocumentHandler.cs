using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Idempotency;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Mappings;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.InventoryTransfers.Commands.RecordPendingTransferSapDocument;

public sealed class RecordPendingTransferSapDocumentHandler(
    ApplicationDbContext context,
    ITransferWarehouseAuthorizer warehouseAuthorizer,
    ISAPServiceLayerClient sapClient,
    IInventoryTransferApprovalService approvalService,
    IIdempotencyRequestStore idempotencyRequestStore,
    IAuditService auditService,
    IOptions<SAPSettings> settings,
    ILogger<RecordPendingTransferSapDocumentHandler> logger)
    : IRequestHandler<RecordPendingTransferSapDocumentCommand, ErrorOr<PendingInventoryTransferDecisionResponseDto>>
{
    public async Task<ErrorOr<PendingInventoryTransferDecisionResponseDto>> Handle(
        RecordPendingTransferSapDocumentCommand command,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled) return Errors.InventoryTransfer.SapDisabled;

        var pending = await context.PendingInventoryTransfers
            .AsTracking()
            .FirstOrDefaultAsync(item => item.Id == command.PendingTransferId, cancellationToken);
        if (pending is null)
            return Errors.InventoryTransfer.PendingTransferNotFound(command.PendingTransferId);

        if (pending.Status is not (PendingInventoryTransferStatuses.Approved or PendingInventoryTransferStatuses.PostFailed))
            return Errors.InventoryTransfer.PendingTransferNotActionable(pending.Status);

        var scopeCheck = await warehouseAuthorizer.EnsureCanActOnSourceAsync(
            command.UserId, pending.FromWarehouse, cancellationToken);
        if (scopeCheck.IsError)
            return scopeCheck.Errors;

        var claimedBy = await context.PendingInventoryTransfers
            .AsNoTracking()
            .Where(other => other.Id != pending.Id && other.SapDocNum == command.SapDocNum)
            .Select(other => other.DraftNumber ?? other.Id.ToString())
            .FirstOrDefaultAsync(cancellationToken);
        if (claimedBy is not null)
            return Errors.InventoryTransfer.SapTransferAlreadyRecorded(command.SapDocNum, claimedBy);

        // Searched by the van's warehouse over the life of the request: the document date can be the
        // requester's own DocDate or the day it posted, and either is inside this window.
        var createdLocal = AuditService.ToCAT(pending.CreatedAtUtc).Date;
        var fromDate = (pending.DocDate is { } docDate && docDate.Date < createdLocal ? docDate.Date : createdLocal).AddDays(-1);
        var toDate = AuditService.ToCAT(DateTime.UtcNow).Date.AddDays(1);

        InventoryTransfer? document;
        try
        {
            var transfers = await sapClient.GetInventoryTransfersByDateRangeAsync(
                pending.ToWarehouse, fromDate, toDate, cancellationToken);
            document = transfers.FirstOrDefault(transfer => transfer.DocNum == command.SapDocNum);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception,
                "Could not read SAP transfers into {Warehouse} to record #{DocNum} against pending transfer {PendingId}",
                pending.ToWarehouse, command.SapDocNum, pending.Id);
            return Errors.InventoryTransfer.SapConnectionError(exception.Message);
        }

        if (document is null)
            return Errors.InventoryTransfer.SapTransferNotFound(command.SapDocNum, pending.ToWarehouse);

        if (!string.Equals(document.FromWarehouse, pending.FromWarehouse, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(document.ToWarehouse, pending.ToWarehouse, StringComparison.OrdinalIgnoreCase))
        {
            return Errors.InventoryTransfer.SapTransferDoesNotMatch(
                command.SapDocNum,
                $"it moves stock from {document.FromWarehouse} to {document.ToWarehouse}, and this request is from " +
                $"{pending.FromWarehouse} to {pending.ToWarehouse}.");
        }

        // The poster's own claim: a post starting now would create a second document, and a post that
        // runs later must replay this one rather than create another.
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
                return Errors.InventoryTransfer.PendingTransferNotActionable(PendingInventoryTransferStatuses.Posted);
            case IdempotencyAcquireOutcome.RequestMismatch:
                return Errors.Idempotency.RequestMismatch("inventory transfer post");
        }

        var transferDto = document.ToDto();
        var completed = false;
        try
        {
            pending.Status = PendingInventoryTransferStatuses.Posted;
            pending.SapDocEntry = document.DocEntry;
            pending.SapDocNum = document.DocNum;
            // The attempt that timed out is the one that created it, so its start is when the van's
            // stock actually moved; "now" would add however long the record sat failed to the wait.
            pending.PostedAtUtc = pending.LastAttemptedAtUtc ?? DateTime.UtcNow;
            pending.PostedByUserId = command.UserId;
            pending.PostRecordedManually = true;
            pending.LastError = null;
            await context.SaveChangesAsync(cancellationToken);

            if (acquired.RequestId is { } requestId)
            {
                await idempotencyRequestStore.CompleteAsync(requestId, transferDto, CancellationToken.None);
                completed = true;
            }
        }
        finally
        {
            if (!completed && acquired.RequestId is { } requestId)
            {
                try { await idempotencyRequestStore.ReleaseAsync(requestId, CancellationToken.None); }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Failed to release the post claim for pending inventory transfer {PendingId}", pending.Id);
                }
            }
        }

        try
        {
            await approvalService.MarkGeneratedAsync(
                ApprovalDocumentTypes.InventoryTransfer, pending.Id.ToString(),
                document.DocEntry, document.DocNum, command.UserId, byAuthorizer: true, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "Recorded SAP transfer #{DocNum} against pending transfer {PendingId} but could not mark its approval generated",
                document.DocNum, pending.Id);
        }

        try
        {
            await auditService.LogAsync(
                AuditActions.RecordPendingTransferSapDocument, "PendingInventoryTransfer", pending.Id.ToString(),
                $"Recorded SAP transfer #{document.DocNum} from {pending.FromWarehouse} to {pending.ToWarehouse}, found in SAP after a failed post",
                true);
        }
        catch { }

        return new PendingInventoryTransferDecisionResponseDto
        {
            PendingTransferId = pending.Id,
            Status = pending.Status,
            ApprovalProcessComplete = true,
            Transfer = transferDto,
            Message = $"Recorded inventory transfer #{document.DocNum} from SAP. Nothing was posted again."
        };
    }
}
