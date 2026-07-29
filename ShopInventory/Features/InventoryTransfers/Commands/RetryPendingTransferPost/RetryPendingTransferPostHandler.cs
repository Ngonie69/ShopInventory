using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models.Entities;
using ShopInventory.Services;

namespace ShopInventory.Features.InventoryTransfers.Commands.RetryPendingTransferPost;

public sealed class RetryPendingTransferPostHandler(
    ApplicationDbContext context,
    ITransferWarehouseAuthorizer warehouseAuthorizer,
    IPendingInventoryTransferPoster poster,
    IOptions<SAPSettings> settings)
    : IRequestHandler<RetryPendingTransferPostCommand, ErrorOr<PendingInventoryTransferDecisionResponseDto>>
{
    public async Task<ErrorOr<PendingInventoryTransferDecisionResponseDto>> Handle(
        RetryPendingTransferPostCommand command,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled) return Errors.InventoryTransfer.SapDisabled;

        var pending = await context.PendingInventoryTransfers
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

        if (pending.Status is not (PendingInventoryTransferStatuses.Approved or PendingInventoryTransferStatuses.PostFailed))
            return Errors.InventoryTransfer.PendingTransferNotActionable(pending.Status);

        var scopeCheck = await warehouseAuthorizer.EnsureCanActOnSourceAsync(
            command.UserId, pending.FromWarehouse, cancellationToken);
        if (scopeCheck.IsError)
            return scopeCheck.Errors;

        var posted = await poster.PostAsync(pending, command.UserId, cancellationToken);
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
