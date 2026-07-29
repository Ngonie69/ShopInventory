using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Mappings;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.InventoryTransfers.Commands.EditTransferRequest;

/// <summary>
/// Changes the lines of an SAP transfer request: quantities may be adjusted and lines dropped.
/// </summary>
/// <remarks>
/// Where the change lands depends on the editor's warehouse scope. Someone who runs the source
/// warehouse is changing their own stock and writes to SAP directly. Anyone scoped to other
/// warehouses is not — their change is held here and only reaches SAP once approved, which is
/// the difference between adjusting your own depot's paperwork and adjusting someone else's.
/// </remarks>
public sealed class EditTransferRequestHandler(
    ApplicationDbContext context,
    ISAPServiceLayerClient sapClient,
    IInventoryTransferApprovalService approvalService,
    ITransferWarehouseAuthorizer warehouseAuthorizer,
    IAuditService auditService,
    IOptions<SAPSettings> settings,
    ILogger<EditTransferRequestHandler> logger)
    : IRequestHandler<EditTransferRequestCommand, ErrorOr<TransferRequestEditResponseDto>>
{
    public async Task<ErrorOr<TransferRequestEditResponseDto>> Handle(
        EditTransferRequestCommand command,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled) return Errors.InventoryTransfer.SapDisabled;

        var requestedLines = command.Request.Lines ?? [];
        if (requestedLines.Count == 0)
            return Errors.InventoryTransfer.ValidationFailed("At least one line must be kept.");

        var editor = await context.Users.AsNoTracking()
            .FirstOrDefaultAsync(user => user.Id == command.UserId && user.IsActive, cancellationToken);
        if (editor is null)
            return Errors.InventoryTransfer.ApproverNotAuthenticated;

        InventoryTransferRequest? document;
        try
        {
            document = await sapClient.GetInventoryTransferRequestByDocEntryAsync(command.DocEntry, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Errors.InventoryTransfer.CreationFailed("Request was canceled by the client");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not read transfer request {DocEntry} from SAP", command.DocEntry);
            return Errors.InventoryTransfer.CreationFailed(exception.Message);
        }

        if (document is null)
            return Errors.InventoryTransfer.TransferRequestNotFound(command.DocEntry);

        if (IsClosed(document.DocumentStatus))
            return Errors.InventoryTransfer.TransferRequestNotEditable;

        var (proposedLines, validationError) = TransferRequestEditMapper.BuildProposedLines(document, requestedLines);
        if (validationError is not null)
            return Errors.InventoryTransfer.ValidationFailed(validationError);

        var originalLines = TransferRequestEditMapper.FromDocument(document);
        if (TransferRequestEditMapper.IsNoOp(originalLines, proposedLines!))
            return Errors.InventoryTransfer.ValidationFailed("The request already reads exactly as submitted.");

        var scopeCheck = await warehouseAuthorizer.EnsureCanActOnSourceAsync(
            command.UserId, document.FromWarehouse, cancellationToken);

        return scopeCheck.IsError
            ? await HoldForApprovalAsync(command, document, editor, originalLines, proposedLines!, cancellationToken)
            : await ApplyDirectlyAsync(command, document, proposedLines!, cancellationToken);
    }

    private async Task<ErrorOr<TransferRequestEditResponseDto>> ApplyDirectlyAsync(
        EditTransferRequestCommand command,
        InventoryTransferRequest document,
        List<TransferRequestEditLineDto> proposedLines,
        CancellationToken cancellationToken)
    {
        try
        {
            var updated = await sapClient.UpdateInventoryTransferRequestLinesAsync(
                command.DocEntry, TransferRequestEditMapper.ToSapLines(proposedLines), cancellationToken);

            try
            {
                await auditService.LogAsync(
                    AuditActions.EditTransferRequest, "TransferRequest", command.DocEntry.ToString(),
                    $"Transfer request #{document.DocNum} changed to {proposedLines.Count} line(s)", true);
            }
            catch { }

            return new TransferRequestEditResponseDto
            {
                Message = $"Transfer request #{document.DocNum} updated.",
                RequestDocEntry = command.DocEntry,
                RequiresApproval = false,
                TransferRequest = updated.ToDto()
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Errors.InventoryTransfer.CreationFailed("Request was canceled by the client");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to update transfer request {DocEntry} in SAP", command.DocEntry);
            return Errors.InventoryTransfer.CreationFailed(exception.Message);
        }
    }

    private async Task<ErrorOr<TransferRequestEditResponseDto>> HoldForApprovalAsync(
        EditTransferRequestCommand command,
        InventoryTransferRequest document,
        User editor,
        List<TransferRequestEditLineDto> originalLines,
        List<TransferRequestEditLineDto> proposedLines,
        CancellationToken cancellationToken)
    {
        // One held change at a time per request: a second proposal would be reviewed against
        // lines the first one is already about to move.
        var alreadyHeld = await context.PendingTransferRequestEdits.AsNoTracking().AnyAsync(
            item => item.RequestDocEntry == command.DocEntry &&
                    item.Status == PendingTransferRequestEditStatuses.AwaitingApproval,
            cancellationToken);
        if (alreadyHeld)
            return Errors.InventoryTransfer.TransferRequestEditInFlight(command.DocEntry);

        var edit = new PendingTransferRequestEditEntity
        {
            RequestDocEntry = document.DocEntry,
            RequestDocNum = document.DocNum,
            FromWarehouse = document.FromWarehouse?.Trim() ?? string.Empty,
            ToWarehouse = document.ToWarehouse?.Trim() ?? string.Empty,
            OriginalLinesJson = TransferRequestEditMapper.Serialize(originalLines),
            ProposedLinesJson = TransferRequestEditMapper.Serialize(proposedLines),
            Status = PendingTransferRequestEditStatuses.AwaitingApproval,
            CreatedByUserId = editor.Id,
            CreatedByName = BuildDisplayName(editor),
            CreatedByRole = editor.Role,
            Reason = Truncate(command.Request.Reason, 500)
        };

        context.PendingTransferRequestEdits.Add(edit);
        await context.SaveChangesAsync(cancellationToken);

        try
        {
            var approvalRequest = await approvalService.EnsureRequestAsync(
                ApprovalDocumentContext.ForRequestEdit(edit), editor.Id, cancellationToken);
            edit.ApprovalRequestId = approvalRequest.Id;
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            // Without an approval request the change could never be actioned, so do not keep it.
            logger.LogError(exception, "Could not open an approval request for transfer request change {EditId}", edit.Id);
            context.PendingTransferRequestEdits.Remove(edit);
            await context.SaveChangesAsync(CancellationToken.None);
            return Errors.InventoryTransfer.InvalidOperation(exception.Message);
        }

        try
        {
            await auditService.LogAsync(
                AuditActions.SubmitTransferRequestEditForApproval, "TransferRequestEdit", edit.Id.ToString(),
                $"Change to transfer request #{document.DocNum} ({document.FromWarehouse} → {document.ToWarehouse}) submitted for approval", true);
        }
        catch { }

        return new TransferRequestEditResponseDto
        {
            Message = $"You are not assigned to warehouse {document.FromWarehouse}, so this change was sent for approval. " +
                      "It will be applied to the transfer request once approved.",
            RequestDocEntry = command.DocEntry,
            RequiresApproval = true,
            PendingEdit = TransferRequestEditMapper.ToDto(edit)
        };
    }

    private static bool IsClosed(string? documentStatus) =>
        string.Equals(documentStatus, "bost_Close", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(documentStatus, "Closed", StringComparison.OrdinalIgnoreCase);

    private static string BuildDisplayName(User user)
    {
        var name = string.Join(' ', new[] { user.FirstName, user.LastName }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        return string.IsNullOrWhiteSpace(name) ? user.Username : name;
    }

    private static string? Truncate(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? null :
        value.Length <= maxLength ? value.Trim() : value.Trim()[..maxLength];
}
