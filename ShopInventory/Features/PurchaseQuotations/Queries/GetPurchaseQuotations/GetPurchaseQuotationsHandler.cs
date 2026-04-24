using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.PurchaseQuotations.Queries.GetPurchaseQuotations;

public sealed class GetPurchaseQuotationsHandler(
    ISAPServiceLayerClient sapClient,
    ILogger<GetPurchaseQuotationsHandler> logger
) : IRequestHandler<GetPurchaseQuotationsQuery, ErrorOr<PurchaseQuotationListResponseDto>>
{
    public async Task<ErrorOr<PurchaseQuotationListResponseDto>> Handle(
        GetPurchaseQuotationsQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            List<SAPPurchaseQuotation> quotations;
            int totalCount;

            if (request.FromDate.HasValue && request.ToDate.HasValue)
            {
                quotations = await sapClient.GetPurchaseQuotationsByDateRangeAsync(request.FromDate.Value, request.ToDate.Value, cancellationToken);
            }
            else if (!string.IsNullOrWhiteSpace(request.CardCode))
            {
                quotations = await sapClient.GetPurchaseQuotationsBySupplierAsync(request.CardCode, cancellationToken);
            }
            else
            {
                quotations = await sapClient.GetPagedPurchaseQuotationsAsync(request.Page, request.PageSize, cancellationToken);
                totalCount = await sapClient.GetPurchaseQuotationsCountAsync(request.CardCode, request.FromDate, request.ToDate, cancellationToken);

                return new PurchaseQuotationListResponseDto
                {
                    Page = request.Page,
                    PageSize = request.PageSize,
                    Count = quotations.Count,
                    TotalCount = totalCount,
                    TotalPages = (int)Math.Ceiling(totalCount / (double)request.PageSize),
                    HasMore = request.Page * request.PageSize < totalCount,
                    Quotations = quotations.Select(PurchaseQuotationMappings.MapFromSap).ToList()
                };
            }

            if (!string.IsNullOrWhiteSpace(request.CardCode))
            {
                quotations = quotations
                    .Where(quotation => string.Equals(quotation.CardCode, request.CardCode, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            totalCount = quotations.Count;
            quotations = quotations
                .Skip((request.Page - 1) * request.PageSize)
                .Take(request.PageSize)
                .ToList();

            return new PurchaseQuotationListResponseDto
            {
                Page = request.Page,
                PageSize = request.PageSize,
                Count = quotations.Count,
                TotalCount = totalCount,
                TotalPages = (int)Math.Ceiling(totalCount / (double)request.PageSize),
                HasMore = request.Page * request.PageSize < totalCount,
                Quotations = quotations.Select(PurchaseQuotationMappings.MapFromSap).ToList()
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching purchase quotations from SAP");
            return Errors.PurchaseQuotation.LoadFailed(ex.Message);
        }
    }
}