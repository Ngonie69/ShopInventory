using ErrorOr;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.InventoryTransfers;
using ShopInventory.Features.Notifications;
using ShopInventory.Mappings;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Services;

/// <summary>
/// Posts a fully approved inventory transfer to SAP.
/// </summary>
public interface IPendingInventoryTransferPoster
{
    /// <summary>
    /// Re-validates stock and posts the held transfer. On success the entity is marked
    /// <see cref="PendingInventoryTransferStatuses.Posted"/>; on failure it is marked
    /// <see cref="PendingInventoryTransferStatuses.PostFailed"/> so it can be retried.
    /// The caller is responsible for saving the tracked entity.
    /// </summary>
    /// <remarks>
    /// Deliberately takes no <see cref="CancellationToken"/>. Every caller reaches here with the
    /// approval already committed, so the post is a durable obligation: handed the request's token
    /// (which ASP.NET binds to <c>HttpContext.RequestAborted</c>), a browser disconnect or a proxy
    /// timeout would abandon the transfer at <see cref="PendingInventoryTransferStatuses.Approved"/>
    /// with no error and no retry offered. The SAP client's own timeout still bounds the call.
    /// </remarks>
    Task<ErrorOr<InventoryTransferDto>> PostAsync(
        PendingInventoryTransferEntity pending,
        Guid postedByUserId);
}

public sealed class PendingInventoryTransferPoster(
    ApplicationDbContext context,
    ISAPServiceLayerClient sapClient,
    IStockValidationService stockValidation,
    IInventoryTransferApprovalService approvalService,
    INotificationService notificationService,
    IAuditService auditService,
    ILogger<PendingInventoryTransferPoster> logger) : IPendingInventoryTransferPoster
{
    public async Task<ErrorOr<InventoryTransferDto>> PostAsync(
        PendingInventoryTransferEntity pending,
        Guid postedByUserId)
    {
        // The approval is already committed, so nothing downstream may cancel this. See the
        // interface for why the caller's request token is not threaded through.
        var cancellationToken = CancellationToken.None;

        CreateInventoryTransferRequest payload;
        try
        {
            payload = PendingInventoryTransferMapper.DeserializePayload(pending);
        }
        catch (InvalidOperationException exception)
        {
            return await FailAsync(pending, exception.Message, cancellationToken);
        }

        // SAP is the only record some people ever see, so the document carries the reason, who
        // raised it and who approved it. The payload is rebuilt from storage on every attempt,
        // so a retried post recomposes these rather than appending to them.
        payload.Comments = InventoryTransferRemarks.Build(
            pending,
            await LoadApprovalProgressAsync(pending, cancellationToken),
            payload.Comments);

        try
        {
            // Stock may have moved while the transfer waited for approval, so this check —
            // not the one taken at submission time — is the authoritative one.
            var stockValidationResult = await stockValidation.ValidateInventoryTransferStockAsync(payload, cancellationToken);
            if (!stockValidationResult.IsValid)
            {
                var message = $"Insufficient stock in source warehouse: " +
                              string.Join("; ", stockValidationResult.Errors.Select(error => error.Message));
                await FailAsync(pending, message, cancellationToken);
                return Errors.InventoryTransfer.InsufficientStock(message);
            }

            var transfer = await sapClient.CreateInventoryTransferAsync(payload, stockValidationResult.PreFetchedData, cancellationToken);
            var transferDto = transfer.ToDto();

            pending.Status = PendingInventoryTransferStatuses.Posted;
            pending.SapDocEntry = transfer.DocEntry;
            pending.SapDocNum = transfer.DocNum;
            pending.PostedAtUtc = DateTime.UtcNow;
            pending.PostedByUserId = postedByUserId;
            pending.LastError = null;
            await context.SaveChangesAsync(cancellationToken);

            await approvalService.MarkGeneratedAsync(
                ApprovalDocumentTypes.InventoryTransfer, pending.Id.ToString(),
                transfer.DocEntry, transfer.DocNum, postedByUserId, byAuthorizer: true, cancellationToken);

            logger.LogInformation(
                "Approved inventory transfer {PendingId} posted to SAP. DocEntry: {DocEntry}, DocNum: {DocNum}",
                pending.Id, transfer.DocEntry, transfer.DocNum);

            try
            {
                await auditService.LogAsync(
                    AuditActions.CreateTransfer, "InventoryTransfer", transfer.DocEntry.ToString(),
                    $"Transfer #{transfer.DocNum} from {pending.FromWarehouse} to {pending.ToWarehouse} posted after approval", true);
            }
            catch { }

            await NotifyPostedAsync(pending, transfer.DocEntry, transfer.DocNum, cancellationToken);
            return transferDto;
        }
        catch (OperationCanceledException exception)
        {
            // Nothing cancels this token, so an OCE here is the SAP client's own timeout: the
            // request reached SAP and the answer was never read. The document may well exist, so
            // the record is failed — visible and retryable — but says so rather than inviting a
            // blind retry that would move the stock twice.
            logger.LogError(exception,
                "The SAP post for approved inventory transfer {PendingId} timed out; the outcome is unknown", pending.Id);
            await FailAsync(
                pending,
                "The SAP post timed out before SAP answered, so it is not known whether the transfer was created. "
                + "Check SAP for this transfer before retrying — retrying will post it again.",
                cancellationToken);
            return Errors.InventoryTransfer.CreationFailed(
                "The transfer was approved, but SAP did not answer in time. Check SAP before retrying the post.");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to post approved inventory transfer {PendingId} to SAP", pending.Id);
            await FailAsync(pending, exception.Message, cancellationToken);
            return Errors.InventoryTransfer.CreationFailed(
                $"The transfer was approved but could not be posted to SAP: {exception.Message}");
        }
    }

    /// <summary>
    /// Approval progress for the remarks. A transfer that cannot be routed still has a reason and
    /// an originator worth recording, so a failure here costs the approver names, not the post.
    /// </summary>
    private async Task<IReadOnlyList<ApprovalStageProgressDto>> LoadApprovalProgressAsync(
        PendingInventoryTransferEntity pending,
        CancellationToken cancellationToken)
    {
        try
        {
            var (_, stages) = await approvalService.GetProgressAsync(
                ApprovalDocumentContext.ForPendingTransfer(pending), cancellationToken);
            return stages;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "Could not read approval progress for the SAP remarks on pending transfer {PendingId}", pending.Id);
            return [];
        }
    }

    private async Task<Error> FailAsync(
        PendingInventoryTransferEntity pending,
        string message,
        CancellationToken cancellationToken)
    {
        pending.Status = PendingInventoryTransferStatuses.PostFailed;
        pending.LastError = message.Length > 2000 ? message[..2000] : message;
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (Exception exception) { logger.LogWarning(exception, "Could not record the post failure for pending transfer {PendingId}", pending.Id); }
        return Errors.InventoryTransfer.CreationFailed(message);
    }

    private async Task NotifyPostedAsync(
        PendingInventoryTransferEntity pending,
        int docEntry,
        int docNum,
        CancellationToken cancellationToken)
    {
        try
        {
            await notificationService.CreateNotificationAsync(
                ModuleNotificationFactory.CreateBroadcastNotification(
                    $"Inventory Transfer Approved: #{docNum}",
                    $"Inventory transfer #{docNum} from {pending.FromWarehouse} to {pending.ToWarehouse}, " +
                    $"requested by {pending.CreatedByName}, was approved and posted to SAP.",
                    "Success",
                    "InventoryTransfer",
                    "InventoryTransfer",
                    docEntry.ToString(),
                    "/inventory-transfers",
                    new Dictionary<string, string>
                    {
                        ["docEntry"] = docEntry.ToString(),
                        ["docNum"] = docNum.ToString(),
                        ["fromWarehouse"] = pending.FromWarehouse,
                        ["toWarehouse"] = pending.ToWarehouse,
                        ["pendingTransferId"] = pending.Id.ToString()
                    }),
                cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to publish the posted-transfer notification for DocEntry {DocEntry}", docEntry);
        }
    }
}
