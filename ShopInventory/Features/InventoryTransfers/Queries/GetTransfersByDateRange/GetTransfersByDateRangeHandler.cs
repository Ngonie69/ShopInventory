using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Mappings;
using ShopInventory.Models;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.InventoryTransfers.Queries.GetTransfersByDateRange;

public sealed class GetTransfersByDateRangeHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> settings,
    ILogger<GetTransfersByDateRangeHandler> logger
) : IRequestHandler<GetTransfersByDateRangeQuery, ErrorOr<InventoryTransferDateResponseDto>>
{
    public async Task<ErrorOr<InventoryTransferDateResponseDto>> Handle(
        GetTransfersByDateRangeQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.InventoryTransfer.SapDisabled;

        if (string.IsNullOrWhiteSpace(request.WarehouseCode))
            return Errors.InventoryTransfer.WarehouseCodeRequired;

        if (!DateTime.TryParse(request.FromDate, out DateTime parsedFromDate))
            return Errors.InventoryTransfer.InvalidDateFormat("FromDate");

        if (!DateTime.TryParse(request.ToDate, out DateTime parsedToDate))
            return Errors.InventoryTransfer.InvalidDateFormat("ToDate");

        if (parsedFromDate > parsedToDate)
            return Errors.InventoryTransfer.InvalidDateRange;

        try
        {
            var usePagination = request.Page.HasValue || request.PageSize.HasValue;
            List<InventoryTransfer> transfers;
            var currentPage = request.Page ?? 1;
            var currentPageSize = request.PageSize ?? 20;
            int totalCount;
            int totalPages;
            bool hasMore;

            if (usePagination)
            {
                currentPage = Math.Max(currentPage, 1);
                currentPageSize = Math.Clamp(currentPageSize, 1, 100);
                var skip = (currentPage - 1) * currentPageSize;

                transfers = await sapClient.GetPagedInventoryTransfersByOffsetAsync(
                    request.WarehouseCode, skip, currentPageSize, parsedFromDate, parsedToDate, cancellationToken);

                totalCount = await sapClient.GetInventoryTransfersCountAsync(
                    request.WarehouseCode, parsedFromDate, parsedToDate, cancellationToken);
                totalPages = currentPageSize > 0 ? (int)Math.Ceiling(totalCount / (double)currentPageSize) : 1;
                hasMore = (currentPage * currentPageSize) < totalCount;
            }
            else
            {
                transfers = await sapClient.GetInventoryTransfersByDateRangeAsync(
                    request.WarehouseCode, parsedFromDate, parsedToDate, cancellationToken);
                totalCount = transfers.Count;
                currentPage = 1;
                currentPageSize = transfers.Count;
                totalPages = totalCount > 0 ? 1 : 0;
                hasMore = false;
            }

            logger.LogInformation("Retrieved {Count} inventory transfers to warehouse {Warehouse} from {FromDate} to {ToDate}",
                transfers.Count, request.WarehouseCode, request.FromDate, request.ToDate);

            return new InventoryTransferDateResponseDto
            {
                Warehouse = request.WarehouseCode,
                FromDate = parsedFromDate.ToString("yyyy-MM-dd"),
                ToDate = parsedToDate.ToString("yyyy-MM-dd"),
                Page = currentPage,
                PageSize = currentPageSize,
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
            logger.LogError(ex, "Timeout retrieving transfers by date range");
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
}
