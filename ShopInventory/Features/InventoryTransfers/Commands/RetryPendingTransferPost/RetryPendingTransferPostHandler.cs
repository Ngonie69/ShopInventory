using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.InventoryTransfers;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.InventoryTransfers.Commands.RetryPendingTransferPost;

public sealed class RetryPendingTransferPostHandler(
    ApplicationDbContext context,
    ITransferWarehouseAuthorizer warehouseAuthorizer,
    IPendingInventoryTransferPoster poster,
    IFactoryTransferSapApproval factoryTransfers,
    IOptions<SAPSettings> settings)
    : IRequestHandler<RetryPendingTransferPostCommand, ErrorOr<PendingInventoryTransferDecisionResponseDto>>
{
    public async Task<ErrorOr<PendingInventoryTransferDecisionResponseDto>> Handle(
        RetryPendingTransferPostCommand command,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled) return Errors.InventoryTransfer.SapDisabled;

        // Tracked: the poster records the outcome on this record.
        var pending = await context.PendingInventoryTransfers
            .AsTracking()
            .FirstOrDefaultAsync(item => item.Id == command.PendingTransferId, cancellationToken);
        if (pending is null)
            return Errors.InventoryTransfer.PendingTransferNotFound(command.PendingTransferId);

        if (pending.Status is PendingInventoryTransferStatuses.Posted)
        {
            return new PendingInventoryTransferDecisionResponseDto
            {
                PendingTransferId = pending.Id,
                Status = pending.Status,
                ApprovalProcessComplete = true,
                Message = $"Inventory transfer #{pending.SapDocNum} has already been posted to SAP."
            };
        }

        var factory = TransferTypes.IsFactory(pending.TransferType);
        var actionable = pending.Status is PendingInventoryTransferStatuses.Approved or PendingInventoryTransferStatuses.PostFailed
                         || (factory && pending.Status == PendingInventoryTransferStatuses.HeldInSap);
        if (!actionable)
            return Errors.InventoryTransfer.PendingTransferNotActionable(pending.Status);

        var scopeCheck = await warehouseAuthorizer.EnsureCanActOnSourceAsync(
            command.UserId, pending.FromWarehouse, cancellationToken);
        if (scopeCheck.IsError)
            return scopeCheck.Errors;

        // A factory transfer goes through SAP's own approval: this saves its draft, says where the draft
        // stands, or adds it once SAP has approved it.
        if (factory)
        {
            var username = await context.Users.AsNoTracking()
                .Where(user => user.Id == command.UserId)
                .Select(user => user.Username)
                .FirstOrDefaultAsync(cancellationToken) ?? $"user {command.UserId}";
            var step = await factoryTransfers.AdvanceAsync(pending, command.UserId, username);
            if (step.IsError)
                return step.Errors;

            return new PendingInventoryTransferDecisionResponseDto
            {
                PendingTransferId = pending.Id,
                Status = pending.Status,
                ApprovalProcessComplete = true,
                Transfer = step.Value.Transfer,
                Message = step.Value.Message
            };
        }

        var posted = await poster.PostAsync(pending, command.UserId);
        if (posted.IsError)
            return posted.Errors;

        return new PendingInventoryTransferDecisionResponseDto
        {
            PendingTransferId = pending.Id,
            Status = pending.Status,
            ApprovalProcessComplete = true,
            Transfer = posted.Value,
            Message = $"Inventory transfer #{posted.Value.DocNum} posted to SAP."
        };
    }
}
