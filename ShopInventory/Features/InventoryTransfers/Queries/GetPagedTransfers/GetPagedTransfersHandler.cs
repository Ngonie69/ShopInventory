using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Mappings;
using ShopInventory.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.InventoryTransfers.Queries.GetPagedTransfers;

/// <remarks>
/// The warehouse's transfer count is a second SAP round trip (<c>StockTransfers/$count</c>) per
/// page. The Web's cache sweeps walk every page of a warehouse, so counting on each one doubled
/// the SAP work of a sweep for a number that barely moves while it runs.
/// <para>
/// Page 1 always counts afresh and stores the figure for <see cref="CountLifetime"/>. A later page
/// reuses the stored figure only when the page came back full and the stored figure still puts
/// rows after it — the middle of a walk, where <c>HasMore</c> is true either way, because stock
/// transfers are not deleted in SAP (a cancellation adds a document) and a count can only have
/// grown since it was taken. Every other page — short, empty, or full but reaching the stored
/// figure — counts afresh, so the end of a walk is decided by a fresh count exactly as before. A
/// walk of N pages costs two counts instead of N. <c>TotalCount</c> on a middle page can trail the
/// live figure by the transfers posted within <see cref="CountLifetime"/>; the count was never
/// read atomically with the page, so it was already approximate by that margin.
/// </para>
/// </remarks>
public sealed class GetPagedTransfersHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> settings,
    IMemoryCache cache,
    ILogger<GetPagedTransfersHandler> logger
) : IRequestHandler<GetPagedTransfersQuery, ErrorOr<InventoryTransferListResponseDto>>
{
    internal static readonly TimeSpan CountLifetime = TimeSpan.FromSeconds(60);

    public async Task<ErrorOr<InventoryTransferListResponseDto>> Handle(
        GetPagedTransfersQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.InventoryTransfer.SapDisabled;

        if (string.IsNullOrWhiteSpace(request.WarehouseCode))
            return Errors.InventoryTransfer.WarehouseCodeRequired;

        try
        {
            var page = request.Page < 1 ? 1 : request.Page;
            var pageSize = request.PageSize < 1 ? 20 : request.PageSize > 100 ? 100 : request.PageSize;

            var transfers = await sapClient.GetPagedInventoryTransfersToWarehouseAsync(request.WarehouseCode, page, pageSize, cancellationToken);
            var totalCount = await GetTransferCountAsync(request.WarehouseCode, page, pageSize, transfers.Count, cancellationToken);
            var totalPages = pageSize > 0 ? (int)Math.Ceiling(totalCount / (double)pageSize) : 1;
            var hasMore = (page * pageSize) < totalCount;

            logger.LogInformation("Retrieved {Count} inventory transfers (page {Page}) to warehouse {Warehouse}", transfers.Count, page, request.WarehouseCode);

            return new InventoryTransferListResponseDto
            {
                Warehouse = request.WarehouseCode,
                Page = page,
                PageSize = pageSize,
                Count = transfers.Count,
                TotalCount = totalCount,
                TotalPages = totalPages,
                HasMore = hasMore,
                Transfers = transfers.ToDto()
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Errors.InventoryTransfer.CreationFailed("Request was canceled by the client");
        }
        catch (OperationCanceledException ex)
        {
            logger.LogError(ex, "Timeout retrieving paged inventory transfers");
            return Errors.InventoryTransfer.SapTimeout;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Network error connecting to SAP Service Layer");
            return Errors.InventoryTransfer.SapConnectionError(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving inventory transfers for warehouse {Warehouse}", request.WarehouseCode);
            return Errors.InventoryTransfer.CreationFailed(ex.Message);
        }
    }

    private async Task<int> GetTransferCountAsync(
        string warehouseCode,
        int page,
        int pageSize,
        int rowsOnPage,
        CancellationToken cancellationToken)
    {
        var key = $"InventoryTransfers:PagedCount:{warehouseCode}";

        if (page > 1
            && rowsOnPage == pageSize
            && cache.TryGetValue(key, out int stored)
            && page * pageSize < stored)
        {
            return stored;
        }

        var count = await sapClient.GetInventoryTransfersCountAsync(warehouseCode, cancellationToken: cancellationToken);
        cache.Set(key, count, CountLifetime);
        return count;
    }
}
