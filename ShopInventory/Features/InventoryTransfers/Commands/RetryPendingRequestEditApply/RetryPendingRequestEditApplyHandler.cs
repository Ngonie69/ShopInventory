using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.InventoryTransfers.Commands.RetryPendingRequestEditApply;

public sealed class RetryPendingRequestEditApplyHandler(
    ApplicationDbContext context,
    ITransferWarehouseAuthorizer warehouseAuthorizer,
    IPendingTransferRequestEditApplier applier,
    IInventoryTransferApprovalService approvalService,
    IOptions<SAPSettings> settings)
    : IRequestHandler<RetryPendingRequestEditApplyCommand, ErrorOr<PendingTransferRequestEditDecisionResponseDto>>
{
    public async Task<ErrorOr<PendingTransferRequestEditDecisionResponseDto>> Handle(
        RetryPendingRequestEditApplyCommand command,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled) return Errors.InventoryTransfer.SapDisabled;

        // Tracked: the applier records the outcome on this record.
        var edit = await context.PendingTransferRequestEdits
            .AsTracking()
            .FirstOrDefaultAsync(item => item.Id == command.PendingEditId, cancellationToken);
        if (edit is null)
            return Errors.InventoryTransfer.PendingEditNotFound(command.PendingEditId);

        if (edit.Status is PendingTransferRequestEditStatuses.Applied)
        {
            return new PendingTransferRequestEditDecisionResponseDto
            {
                PendingEditId = edit.Id,
                Decision = ApprovalDecisionValues.Approved,
                Status = edit.Status,
                ApprovalProcessComplete = true,
                Message = $"The change to transfer request #{edit.RequestDocNum} has already been applied in SAP."
            };
        }

        if (edit.Status is not PendingTransferRequestEditStatuses.ApplyFailed)
            return Errors.InventoryTransfer.PendingEditNotActionable(edit.Status);

        var scopeCheck = await warehouseAuthorizer.EnsureCanActOnSourceAsync(
            command.UserId, edit.FromWarehouse, cancellationToken);
        if (scopeCheck.IsError)
            return scopeCheck.Errors;

        var applied = await applier.ApplyAsync(edit, command.UserId, cancellationToken);
        if (applied.IsError)
            return applied.Errors;

        // The approval completed when the decision was recorded; only the SAP write was
        // outstanding, so the approval record is closed off here rather than at the decision.
        await approvalService.MarkGeneratedAsync(
            ApprovalDocumentTypes.InventoryTransferRequestEdit, edit.Id.ToString(),
            edit.RequestDocEntry, edit.RequestDocNum, command.UserId, byAuthorizer: true, cancellationToken);

        return new PendingTransferRequestEditDecisionResponseDto
        {
            PendingEditId = edit.Id,
            Decision = ApprovalDecisionValues.Approved,
            Status = edit.Status,
            ApprovalProcessComplete = true,
            TransferRequest = applied.Value,
            Message = $"Transfer request #{edit.RequestDocNum} updated in SAP."
        };
    }
}
