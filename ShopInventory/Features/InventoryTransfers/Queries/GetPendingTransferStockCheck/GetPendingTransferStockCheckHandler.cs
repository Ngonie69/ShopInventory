using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.Services;

namespace ShopInventory.Features.InventoryTransfers.Queries.GetPendingTransferStockCheck;

public sealed class GetPendingTransferStockCheckHandler(
    ApplicationDbContext context,
    ITransferWarehouseAuthorizer warehouseAuthorizer,
    IStockValidationService stockValidation)
    : IRequestHandler<GetPendingTransferStockCheckQuery, ErrorOr<PendingTransferStockCheckResult>>
{
    public async Task<ErrorOr<PendingTransferStockCheckResult>> Handle(
        GetPendingTransferStockCheckQuery query,
        CancellationToken cancellationToken)
    {
        var pending = await context.PendingInventoryTransfers
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == query.PendingTransferId, cancellationToken);
        if (pending is null)
            return Errors.InventoryTransfer.PendingTransferNotFound(query.PendingTransferId);

        var scopeCheck = await warehouseAuthorizer.EnsureCanActOnSourceAsync(
            query.UserId, pending.FromWarehouse, cancellationToken);
        if (scopeCheck.IsError)
            return scopeCheck.Errors;

        CreateInventoryTransferRequest payload;
        try
        {
            payload = PendingInventoryTransferMapper.DeserializePayload(pending);
        }
        catch (InvalidOperationException exception)
        {
            return Errors.InventoryTransfer.ValidationFailed(exception.Message);
        }

        var validation = await stockValidation.ValidateInventoryTransferStockAsync(payload, cancellationToken);

        // A batch shortage is summed over every line naming the batch and reported against the
        // first, so a line can be short without an error of its own; that is still the right line
        // to flag, since it is the one a partial post drops first.
        var errorsByLine = validation.Errors
            .GroupBy(error => error.LineNumber)
            .ToDictionary(group => group.Key, group => group.ToList());

        var lines = payload.Lines!
            .Select((line, index) =>
            {
                var lineNumber = index + 1;
                var warehouse = line.FromWarehouseCode ?? payload.FromWarehouse ?? pending.FromWarehouse;
                errorsByLine.TryGetValue(lineNumber, out var errors);

                var state = errors is not null && errors.Any(error => error.StockReadFailed)
                            || validation.UnreadableWarehouses.Contains(warehouse)
                    ? PendingTransferStockLineStates.Unread
                    : errors is { Count: > 0 }
                        ? PendingTransferStockLineStates.Short
                        : PendingTransferStockLineStates.InStock;

                return new PendingTransferStockCheckLineResult(
                    LineNumber: lineNumber,
                    ItemCode: line.ItemCode ?? string.Empty,
                    UoMCode: line.UoMCode,
                    BatchNumber: errors?.FirstOrDefault(error => error.BatchNumber is not null)?.BatchNumber,
                    Quantity: line.Quantity,
                    AvailableQuantity: state == PendingTransferStockLineStates.Short
                        ? errors!.Min(error => Math.Max(0, error.AvailableQuantity))
                        : null,
                    State: state);
            })
            // Short lines first: they are what the reader came to see.
            .OrderBy(line => line.State switch
            {
                PendingTransferStockLineStates.Short => 0,
                PendingTransferStockLineStates.Unread => 1,
                _ => 2
            })
            .ThenBy(line => line.LineNumber)
            .ToList();

        return new PendingTransferStockCheckResult(
            PendingTransferId: pending.Id,
            FromWarehouse: pending.FromWarehouse,
            ToWarehouse: pending.ToWarehouse,
            CheckedAt: AuditService.ToCAT(DateTime.UtcNow),
            StockWasFullyRead: validation.StockWasFullyRead,
            UnreadableWarehouses: validation.UnreadableWarehouses.ToList(),
            Lines: lines);
    }
}
