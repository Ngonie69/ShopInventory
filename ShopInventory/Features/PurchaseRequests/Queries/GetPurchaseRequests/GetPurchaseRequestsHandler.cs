using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.PurchaseRequests.Queries.GetPurchaseRequests;

public sealed class GetPurchaseRequestsHandler(
    ISAPServiceLayerClient sapClient,
    ILogger<GetPurchaseRequestsHandler> logger
) : IRequestHandler<GetPurchaseRequestsQuery, ErrorOr<PurchaseRequestListResponseDto>>
{
    public async Task<ErrorOr<PurchaseRequestListResponseDto>> Handle(
        GetPurchaseRequestsQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            List<SAPPurchaseRequest> purchaseRequests;
            int totalCount;

            if (request.FromDate.HasValue && request.ToDate.HasValue)
            {
                purchaseRequests = await sapClient.GetPurchaseRequestsByDateRangeAsync(request.FromDate.Value, request.ToDate.Value, cancellationToken);
            }
            else
            {
                purchaseRequests = await sapClient.GetPagedPurchaseRequestsAsync(request.Page, request.PageSize, cancellationToken);
                totalCount = await sapClient.GetPurchaseRequestsCountAsync(request.FromDate, request.ToDate, cancellationToken);

                return new PurchaseRequestListResponseDto
                {
                    Page = request.Page,
                    PageSize = request.PageSize,
                    Count = purchaseRequests.Count,
                    TotalCount = totalCount,
                    TotalPages = (int)Math.Ceiling(totalCount / (double)request.PageSize),
                    HasMore = request.Page * request.PageSize < totalCount,
                    Requests = purchaseRequests.Select(PurchaseRequestMappings.MapFromSap).ToList()
                };
            }

            totalCount = purchaseRequests.Count;
            purchaseRequests = purchaseRequests
                .Skip((request.Page - 1) * request.PageSize)
                .Take(request.PageSize)
                .ToList();

            return new PurchaseRequestListResponseDto
            {
                Page = request.Page,
                PageSize = request.PageSize,
                Count = purchaseRequests.Count,
                TotalCount = totalCount,
                TotalPages = (int)Math.Ceiling(totalCount / (double)request.PageSize),
                HasMore = request.Page * request.PageSize < totalCount,
                Requests = purchaseRequests.Select(PurchaseRequestMappings.MapFromSap).ToList()
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching purchase requests from SAP");
            return Errors.PurchaseRequest.LoadFailed(ex.Message);
        }
    }
}