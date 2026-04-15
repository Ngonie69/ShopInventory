using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Mappings;
using ShopInventory.Models;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.Invoices.Queries.GetInvoicesByCustomer;

public sealed class GetInvoicesByCustomerHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> settings,
    ILogger<GetInvoicesByCustomerHandler> logger
) : IRequestHandler<GetInvoicesByCustomerQuery, ErrorOr<InvoiceDateResponseDto>>
{
    public async Task<ErrorOr<InvoiceDateResponseDto>> Handle(
        GetInvoicesByCustomerQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.Invoice.SapDisabled;

        if (string.IsNullOrWhiteSpace(request.CardCode))
            return Errors.Invoice.CustomerCodeRequired;

        try
        {
            var filterFromDate = request.FromDate.HasValue && request.ToDate.HasValue ? request.FromDate : null;
            var filterToDate = request.FromDate.HasValue && request.ToDate.HasValue ? request.ToDate : null;
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

                invoices = await sapClient.GetPagedInvoicesByOffsetAsync(skip, currentPageSize, null, request.CardCode, filterFromDate, filterToDate, cancellationToken);
                totalCount = await sapClient.GetInvoicesCountAsync(null, request.CardCode, filterFromDate, filterToDate, cancellationToken);
                totalPages = currentPageSize > 0 ? (int)Math.Ceiling(totalCount / (double)currentPageSize) : 1;
                hasMore = (currentPage * currentPageSize) < totalCount;
            }
            else if (filterFromDate.HasValue && filterToDate.HasValue)
            {
                invoices = await sapClient.GetInvoicesByCustomerAsync(request.CardCode, filterFromDate.Value, filterToDate.Value, cancellationToken);
                currentPage = 1;
                currentPageSize = invoices.Count;
                totalCount = invoices.Count;
                totalPages = invoices.Count > 0 ? 1 : 0;
                hasMore = false;
            }
            else
            {
                invoices = await sapClient.GetInvoicesByCustomerAsync(request.CardCode, cancellationToken);
                currentPage = 1;
                currentPageSize = invoices.Count;
                totalCount = invoices.Count;
                totalPages = invoices.Count > 0 ? 1 : 0;
                hasMore = false;
            }

            logger.LogInformation("Retrieved {Count} invoices for customer {CardCode}", invoices.Count, request.CardCode);

            return new InvoiceDateResponseDto
            {
                Customer = request.CardCode,
                FromDate = filterFromDate?.ToString("yyyy-MM-dd"),
                ToDate = filterToDate?.ToString("yyyy-MM-dd"),
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
            logger.LogError(ex, "Error retrieving invoices for customer {CardCode}", request.CardCode);
            return Errors.Invoice.CreationFailed(ex.Message);
        }
    }
}
