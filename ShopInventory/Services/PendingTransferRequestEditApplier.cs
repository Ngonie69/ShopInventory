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
/// Writes an approved change to its SAP transfer request.
/// </summary>
public interface IPendingTransferRequestEditApplier
{
    /// <summary>
    /// Applies the held change. On success the entity is marked
    /// <see cref="PendingTransferRequestEditStatuses.Applied"/>; on failure
    /// <see cref="PendingTransferRequestEditStatuses.ApplyFailed"/> so it can be retried.
    /// </summary>
    Task<ErrorOr<InventoryTransferRequestDto>> ApplyAsync(
        PendingTransferRequestEditEntity edit,
        Guid appliedByUserId,
        CancellationToken cancellationToken);
}

public sealed class PendingTransferRequestEditApplier(
    ApplicationDbContext context,
    ISAPServiceLayerClient sapClient,
    INotificationService notificationService,
    IAuditService auditService,
    ILogger<PendingTransferRequestEditApplier> logger) : IPendingTransferRequestEditApplier
{
    public async Task<ErrorOr<InventoryTransferRequestDto>> ApplyAsync(
        PendingTransferRequestEditEntity edit,
        Guid appliedByUserId,
        CancellationToken cancellationToken)
    {
        var proposedLines = TransferRequestEditMapper.Deserialize(edit.ProposedLinesJson);
        if (proposedLines.Count == 0)
        {
            return await FailAsync(
                edit, "The stored change could not be read, so nothing was written to SAP.", cancellationToken);
        }

        try
        {
            // The request may have moved on while the change waited, so re-read rather than
            // trusting the line numbers captured at proposal time.
            var current = await sapClient.GetInventoryTransferRequestByDocEntryAsync(edit.RequestDocEntry, cancellationToken);
            if (current is null)
            {
                await FailAsync(edit, $"Transfer request {edit.RequestDocEntry} no longer exists in SAP.", cancellationToken);
                return Errors.InventoryTransfer.TransferRequestNotFound(edit.RequestDocEntry);
            }

            var currentLineNums = TransferRequestEditMapper.FromDocument(current)
                .Select(line => line.LineNum)
                .ToHashSet();
            var missing = proposedLines.Where(line => !currentLineNums.Contains(line.LineNum)).ToList();
            if (missing.Count > 0)
            {
                var message = "The transfer request changed while this was awaiting approval: line " +
                              $"{string.Join(", ", missing.Select(line => line.LineNum))} no longer exists. " +
                              "Propose the change again.";
                await FailAsync(edit, message, cancellationToken);
                return Errors.InventoryTransfer.InvalidOperation(message);
            }

            var updated = await sapClient.UpdateInventoryTransferRequestLinesAsync(
                edit.RequestDocEntry, TransferRequestEditMapper.ToSapLines(proposedLines), cancellationToken);

            edit.Status = PendingTransferRequestEditStatuses.Applied;
            edit.AppliedAtUtc = DateTime.UtcNow;
            edit.LastError = null;
            await context.SaveChangesAsync(cancellationToken);

            logger.LogInformation(
                "Approved change {EditId} applied to transfer request {DocEntry}", edit.Id, edit.RequestDocEntry);

            try
            {
                await auditService.LogAsync(
                    AuditActions.EditTransferRequest, "TransferRequest", edit.RequestDocEntry.ToString(),
                    $"Transfer request #{edit.RequestDocNum} changed to {proposedLines.Count} line(s) after approval", true);
            }
            catch { }

            await NotifyAppliedAsync(edit, cancellationToken);
            return updated.ToDto();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Errors.InventoryTransfer.CreationFailed("Request was canceled by the client");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to apply approved change {EditId} to SAP", edit.Id);
            await FailAsync(edit, exception.Message, cancellationToken);
            return Errors.InventoryTransfer.CreationFailed(
                $"The change was approved but could not be written to SAP: {exception.Message}");
        }
    }

    private async Task<Error> FailAsync(
        PendingTransferRequestEditEntity edit,
        string message,
        CancellationToken cancellationToken)
    {
        edit.Status = PendingTransferRequestEditStatuses.ApplyFailed;
        edit.LastError = message.Length > 2000 ? message[..2000] : message;
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (Exception exception) { logger.LogWarning(exception, "Could not record the failure for change {EditId}", edit.Id); }
        return Errors.InventoryTransfer.CreationFailed(message);
    }

    private async Task NotifyAppliedAsync(PendingTransferRequestEditEntity edit, CancellationToken cancellationToken)
    {
        try
        {
            await notificationService.CreateNotificationAsync(
                ModuleNotificationFactory.CreateBroadcastNotification(
                    $"Transfer Request Updated: #{edit.RequestDocNum}",
                    $"An approved change to transfer request #{edit.RequestDocNum} " +
                    $"({edit.FromWarehouse} → {edit.ToWarehouse}), proposed by {edit.CreatedByName}, was applied in SAP.",
                    "Success",
                    "TransferRequest",
                    "TransferRequest",
                    edit.RequestDocEntry.ToString(),
                    "/inventory-transfers",
                    new Dictionary<string, string>
                    {
                        ["docEntry"] = edit.RequestDocEntry.ToString(),
                        ["docNum"] = edit.RequestDocNum.ToString(),
                        ["fromWarehouse"] = edit.FromWarehouse,
                        ["toWarehouse"] = edit.ToWarehouse,
                        ["pendingEditId"] = edit.Id.ToString()
                    }),
                cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to publish the applied-change notification for {EditId}", edit.Id);
        }
    }
}
