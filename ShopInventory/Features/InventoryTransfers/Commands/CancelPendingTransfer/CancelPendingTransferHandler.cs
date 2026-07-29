using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.InventoryTransfers.Commands.CancelPendingTransfer;

public sealed class CancelPendingTransferHandler(
    ApplicationDbContext context,
    IAuditService auditService)
    : IRequestHandler<CancelPendingTransferCommand, ErrorOr<PendingInventoryTransferDecisionResponseDto>>
{
    public async Task<ErrorOr<PendingInventoryTransferDecisionResponseDto>> Handle(
        CancelPendingTransferCommand command,
        CancellationToken cancellationToken)
    {
        var pending = await context.PendingInventoryTransfers
            .AsTracking()
            .FirstOrDefaultAsync(item => item.Id == command.PendingTransferId, cancellationToken);
        if (pending is null)
            return Errors.InventoryTransfer.PendingTransferNotFound(command.PendingTransferId);

        if (pending.Status != PendingInventoryTransferStatuses.AwaitingApproval)
            return Errors.InventoryTransfer.PendingTransferNotActionable(pending.Status);

        var user = await context.Users.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == command.UserId && item.IsActive, cancellationToken);
        if (user is null)
            return Errors.InventoryTransfer.ApproverNotAuthenticated;

        var isAdmin = string.Equals(user.Role, ApplicationRoles.Admin, StringComparison.OrdinalIgnoreCase);
        if (pending.CreatedByUserId != command.UserId && !isAdmin)
            return Error.Forbidden(
                "InventoryTransfer.CancelNotAllowed",
                "Only the person who submitted this transfer, or an administrator, can withdraw it.");

        pending.Status = PendingInventoryTransferStatuses.Cancelled;
        pending.DecidedAtUtc = DateTime.UtcNow;

        if (pending.ApprovalRequestId is { } approvalRequestId)
        {
            var approvalRequest = await context.ApprovalRequests
                .AsTracking()
                .FirstOrDefaultAsync(item => item.Id == approvalRequestId, cancellationToken);
            if (approvalRequest is not null)
            {
                approvalRequest.Status = ApprovalRequestStatuses.Cancelled;
                approvalRequest.CompletedAtUtc ??= DateTime.UtcNow;
            }
        }

        await context.SaveChangesAsync(cancellationToken);

        try
        {
            await auditService.LogAsync(
                AuditActions.CancelPendingTransfer, "PendingInventoryTransfer", pending.Id.ToString(),
                $"Withdrew transfer from {pending.FromWarehouse} to {pending.ToWarehouse}", true);
        }
        catch { }

        return new PendingInventoryTransferDecisionResponseDto
        {
            PendingTransferId = pending.Id,
            Status = pending.Status,
            Decision = ApprovalRequestStatuses.Cancelled,
            Message = "The inventory transfer was withdrawn and will not be posted to SAP."
        };
    }
}
