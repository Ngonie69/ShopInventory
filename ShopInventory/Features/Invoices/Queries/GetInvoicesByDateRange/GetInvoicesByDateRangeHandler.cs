using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Mappings;
using ShopInventory.Models;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.Invoices.Queries.GetInvoicesByDateRange;

public sealed class GetInvoicesByDateRangeHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> settings,
    ILogger<GetInvoicesByDateRangeHandler> logger
) : IRequestHandler<GetInvoicesByDateRangeQuery, ErrorOr<InvoiceDateResponseDto>>
{
    public async Task<ErrorOr<InvoiceDateResponseDto>> Handle(
        GetInvoicesByDateRangeQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.Invoice.SapDisabled;

        if (request.FromDate > request.ToDate)
            return Errors.Invoice.InvalidDateRange;

        try
        {
            var usePagination = request.Page.HasValue || request.PageSize.HasValue;
            List<Invoice> invoices;
            int currentPage;
            int currentPageSize;
            int totalCount;
            int totalPages;
            bool hasMore;

            if (usePagination)
            {
                currentPage = Math.Max(request.Page ?? 1, 1);
                currentPageSize = Math.Clamp(request.PageSize ?? 20, 1, 100);
                var skip = (currentPage - 1) * currentPageSize;

                invoices = await sapClient.GetPagedInvoicesByOffsetAsync(skip, currentPageSize, null, null, request.FromDate, request.ToDate, cancellationToken);
                totalCount = await sapClient.GetInvoicesCountAsync(null, null, request.FromDate, request.ToDate, cancellationToken);
                totalPages = currentPageSize > 0 ? (int)Math.Ceiling(totalCount / (double)currentPageSize) : 1;
                hasMore = (currentPage * currentPageSize) < totalCount;
            }
            else
            {
                invoices = await sapClient.GetInvoicesByDateRangeAsync(request.FromDate, request.ToDate, cancellationToken);
                currentPage = 1;
                currentPageSize = invoices.Count;
                totalCount = invoices.Count;
                totalPages = invoices.Count > 0 ? 1 : 0;
                hasMore = false;
            }

            logger.LogInformation("Retrieved {Count} invoices between {FromDate} and {ToDate}",
                invoices.Count, request.FromDate.ToString("yyyy-MM-dd"), request.ToDate.ToString("yyyy-MM-dd"));

            return new InvoiceDateResponseDto
            {
                FromDate = request.FromDate.ToString("yyyy-MM-dd"),
                ToDate = request.ToDate.ToString("yyyy-MM-dd"),
                Page = currentPage,
                PageSize = currentPageSize,
                Count = invoices.Count,
                TotalCount = totalCount,
                TotalPages = totalPages,
                HasMore = hasMore,
                Invoices = invoices.ToDto()
            };
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            logger.LogError(ex, "Timeout connecting to SAP Service Layer");
            return Errors.Invoice.SapTimeout;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Network error connecting to SAP Service Layer");
            return Errors.Invoice.SapConnectionError(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving invoices by date range");
            return Errors.Invoice.CreationFailed(ex.Message);
        }
    }
}
