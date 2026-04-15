using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Mappings;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.InventoryTransfers.Queries.GetPagedTransfers;

public sealed class GetPagedTransfersHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> settings,
    ILogger<GetPagedTransfersHandler> logger
) : IRequestHandler<GetPagedTransfersQuery, ErrorOr<InventoryTransferListResponseDto>>
{
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
            var totalCount = await sapClient.GetInventoryTransfersCountAsync(request.WarehouseCode, cancellationToken: cancellationToken);
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
}
