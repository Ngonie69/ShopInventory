using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Mappings;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.InventoryTransfers.Queries.GetPagedTransferRequests;

public sealed class GetPagedTransferRequestsHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> settings,
    ILogger<GetPagedTransferRequestsHandler> logger
) : IRequestHandler<GetPagedTransferRequestsQuery, ErrorOr<TransferRequestListResponseDto>>
{
    public async Task<ErrorOr<TransferRequestListResponseDto>> Handle(
        GetPagedTransferRequestsQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.InventoryTransfer.SapDisabled;

        try
        {
            var page = request.Page < 1 ? 1 : request.Page;
            var pageSize = request.PageSize < 1 ? 20 : request.PageSize > 100 ? 100 : request.PageSize;

            var transferRequests = await sapClient.GetPagedInventoryTransferRequestsAsync(page, pageSize, cancellationToken);

            logger.LogInformation("Retrieved {Count} transfer requests (page {Page})", transferRequests.Count, page);

            return new TransferRequestListResponseDto
            {
                Page = page,
                PageSize = pageSize,
                Count = transferRequests.Count,
                HasMore = transferRequests.Count == pageSize,
                TransferRequests = transferRequests.ToDto()
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Errors.InventoryTransfer.CreationFailed("Request was canceled by the client");
        }
        catch (OperationCanceledException ex)
        {
            logger.LogError(ex, "Timeout retrieving paged transfer requests");
            return Errors.InventoryTransfer.SapTimeout;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Network error connecting to SAP Service Layer");
            return Errors.InventoryTransfer.SapConnectionError(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving transfer requests");
            return Errors.InventoryTransfer.CreationFailed(ex.Message);
        }
    }
}
