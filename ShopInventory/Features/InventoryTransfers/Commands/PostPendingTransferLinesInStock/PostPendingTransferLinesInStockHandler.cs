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

namespace ShopInventory.Features.InventoryTransfers.Commands.PostPendingTransferLinesInStock;

public sealed class PostPendingTransferLinesInStockHandler(
    ApplicationDbContext context,
    ITransferWarehouseAuthorizer warehouseAuthorizer,
    IPendingInventoryTransferPoster poster,
    IAuditService auditService,
    IOptions<SAPSettings> settings)
    : IRequestHandler<PostPendingTransferLinesInStockCommand, ErrorOr<PendingInventoryTransferDecisionResponseDto>>
{
    public async Task<ErrorOr<PendingInventoryTransferDecisionResponseDto>> Handle(
        PostPendingTransferLinesInStockCommand command,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled) return Errors.InventoryTransfer.SapDisabled;

        // Tracked: the poster records the outcome on this record.
        var pending = await context.PendingInventoryTransfers
            .AsTracking()
            .FirstOrDefaultAsync(item => item.Id == command.PendingTransferId, cancellationToken);
        if (pending is null)
            return Errors.InventoryTransfer.PendingTransferNotFound(command.PendingTransferId);

        if (pending.Status is not (PendingInventoryTransferStatuses.Approved or PendingInventoryTransferStatuses.PostFailed))
            return Errors.InventoryTransfer.PendingTransferNotActionable(pending.Status);

        // A post whose answer was lost may already be in SAP. Posting part of it beside the whole
        // would move most of the stock twice, so SAP has to be checked first.
        if (pending.Status == PendingInventoryTransferStatuses.PostFailed
            && PendingTransferFailureClassifier.Classify(pending.LastError).Kind == PendingTransferFailureKinds.OutcomeUnknown)
        {
            return Errors.InventoryTransfer.PostOutcomeUnknown;
        }

        var scopeCheck = await warehouseAuthorizer.EnsureCanActOnSourceAsync(
            command.UserId, pending.FromWarehouse, cancellationToken);
        if (scopeCheck.IsError)
            return scopeCheck.Errors;

        var requestedLines = pending.LineCount;
        var posted = await poster.PostAsync(pending, command.UserId, postOnlyLinesInStock: true);
        if (posted.IsError)
            return posted.Errors;

        var dropped = PendingTransferDroppedLines.Read(pending.DroppedLinesJson);

        try
        {
            await auditService.LogAsync(
                AuditActions.PostPendingTransferLinesInStock, "PendingInventoryTransfer", pending.Id.ToString(),
                dropped.Count == 0
                    ? $"Posted transfer #{posted.Value.DocNum} from {pending.FromWarehouse} to {pending.ToWarehouse} whole; every line was in stock"
                    : $"Posted transfer #{posted.Value.DocNum} from {pending.FromWarehouse} to {pending.ToWarehouse} with " +
                      $"{pending.LineCount} of {requestedLines} lines; left out {string.Join(", ", dropped.Select(line => line.ItemCode))}",
                true);
        }
        catch { }

        return new PendingInventoryTransferDecisionResponseDto
        {
            PendingTransferId = pending.Id,
            Status = pending.Status,
            ApprovalProcessComplete = true,
            Transfer = posted.Value,
            Message = dropped.Count == 0
                ? $"Every line was in stock, so inventory transfer #{posted.Value.DocNum} posted whole."
                : $"Inventory transfer #{posted.Value.DocNum} posted {pending.LineCount} of {requestedLines} lines. " +
                  $"{dropped.Count} short line{(dropped.Count == 1 ? " was" : "s were")} left out."
        };
    }
}
