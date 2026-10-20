using ErrorOr;
using MediatR;
using Microsoft.Extensions.Options;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Stock;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.InventoryTransfers.Queries.GetTransferRequestStock;

/// <summary>
/// Reads the source warehouse's stock of every item on a transfer request.
/// </summary>
/// <remarks>
/// <para>Measured the way the conversion is: <see cref="InventoryTransferRequestConversion"/> builds
/// the transfer, so each line's source warehouse is the one it would post from, and what a till has
/// sold that SAP does not have yet is taken off, as the conversion refuses to take those units.</para>
///
/// <para>A warehouse that does not answer is reported as unread rather than as empty: telling someone
/// a depot holds nothing when it was never asked is how a good request gets cut down.</para>
/// </remarks>
public sealed class GetTransferRequestStockHandler(
    ISAPServiceLayerClient sapClient,
    IUnpostedTillClaims tillClaims,
    IOptions<SAPSettings> settings,
    ILogger<GetTransferRequestStockHandler> logger)
    : IRequestHandler<GetTransferRequestStockQuery, ErrorOr<TransferRequestStockResult>>
{
    public async Task<ErrorOr<TransferRequestStockResult>> Handle(
        GetTransferRequestStockQuery query,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled) return Errors.InventoryTransfer.SapDisabled;

        var document = await sapClient.GetInventoryTransferRequestByDocEntryAsync(query.DocEntry, cancellationToken);
        if (document is null)
            return Errors.InventoryTransfer.TransferRequestNotFound(query.DocEntry);

        var overrideWarehouse = string.IsNullOrWhiteSpace(query.FromWarehouse) ? null : query.FromWarehouse.Trim();
        var transfer = InventoryTransferRequestConversion.ToTransfer(document);
        var lines = (transfer.Lines ?? [])
            .Zip(document.StockTransferLines ?? [], (line, requestLine) => (
                requestLine.LineNum,
                ItemCode: line.ItemCode?.Trim() ?? string.Empty,
                Warehouse: overrideWarehouse ?? line.FromWarehouseCode!.Trim(),
                line.Quantity))
            .ToList();

        var extraWarehouse = overrideWarehouse ?? document.FromWarehouse?.Trim();
        var wanted = lines
            .Where(line => line.ItemCode.Length > 0)
            .Select(line => (Warehouse: line.Warehouse, ItemCode: line.ItemCode))
            .Concat(string.IsNullOrWhiteSpace(extraWarehouse)
                ? []
                : (query.ExtraItemCodes ?? [])
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .Select(code => (Warehouse: extraWarehouse, ItemCode: code.Trim())))
            .ToList();

        var items = new Dictionary<(string Warehouse, string ItemCode), TransferRequestItemStockResult>(
            WarehouseItemComparer.Instance);
        var unreadable = new List<string>();

        foreach (var warehouse in wanted.GroupBy(item => item.Warehouse, StringComparer.OrdinalIgnoreCase))
        {
            var codes = warehouse.Select(item => item.ItemCode).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            List<StockQuantityDto>? stock;
            try
            {
                stock = await sapClient.GetStockQuantitiesForItemsInWarehouseAsync(warehouse.Key, codes, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception,
                    "Could not read stock in {Warehouse} for transfer request {DocEntry}", warehouse.Key, query.DocEntry);
                stock = null;
                unreadable.Add(warehouse.Key);
            }

            var held = await tillClaims.ForWarehouseAsync(warehouse.Key, cancellationToken);

            foreach (var code in codes)
            {
                // An item SAP has no row for in this warehouse holds none of it.
                decimal? inStock = stock is null
                    ? null
                    : stock.Where(row => string.Equals(row.ItemCode, code, StringComparison.OrdinalIgnoreCase))
                        .Sum(row => row.Issuable);
                var heldByTills = held.TryGetValue(code, out var claim) ? claim : 0m;

                items[(warehouse.Key, code)] = new TransferRequestItemStockResult(
                    warehouse.Key,
                    code,
                    inStock,
                    heldByTills,
                    inStock is { } onHand ? Math.Max(0m, onHand - heldByTills) : null);
            }
        }

        var demand = lines
            .GroupBy(line => (line.Warehouse, line.ItemCode), WarehouseItemComparer.Instance)
            .ToDictionary(group => group.Key, group => group.Sum(line => line.Quantity), WarehouseItemComparer.Instance);

        var lineResults = lines
            .Select(line =>
            {
                items.TryGetValue((line.Warehouse, line.ItemCode), out var item);
                var available = item?.Available;
                var state = available is not { } known
                    ? TransferRequestStockLineStates.Unread
                    : demand[(line.Warehouse, line.ItemCode)] > known
                        ? TransferRequestStockLineStates.Short
                        : TransferRequestStockLineStates.InStock;

                return new TransferRequestStockLineResult(
                    line.LineNum, line.ItemCode, line.Warehouse, line.Quantity, available, state);
            })
            .ToList();

        return new TransferRequestStockResult(
            DocEntry: document.DocEntry,
            DocNum: document.DocNum,
            CheckedAt: AuditService.ToCAT(DateTime.UtcNow),
            StockWasFullyRead: unreadable.Count == 0,
            UnreadableWarehouses: unreadable,
            Lines: lineResults,
            Items: items.Values.OrderBy(item => item.WarehouseCode).ThenBy(item => item.ItemCode).ToList());
    }

    private sealed class WarehouseItemComparer : IEqualityComparer<(string Warehouse, string ItemCode)>
    {
        public static readonly WarehouseItemComparer Instance = new();

        public bool Equals((string Warehouse, string ItemCode) x, (string Warehouse, string ItemCode) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Warehouse, y.Warehouse) &&
            StringComparer.OrdinalIgnoreCase.Equals(x.ItemCode, y.ItemCode);

        public int GetHashCode((string Warehouse, string ItemCode) obj) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Warehouse),
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.ItemCode));
    }
}
