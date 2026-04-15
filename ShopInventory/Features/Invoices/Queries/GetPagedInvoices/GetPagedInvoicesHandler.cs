using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Configuration;
using ShopInventory.DTOs;
using ShopInventory.Mappings;
using ShopInventory.Services;
using Microsoft.Extensions.Options;

namespace ShopInventory.Features.Invoices.Queries.GetPagedInvoices;

public sealed class GetPagedInvoicesHandler(
    ISAPServiceLayerClient sapClient,
    IOptions<SAPSettings> settings,
    ILogger<GetPagedInvoicesHandler> logger
) : IRequestHandler<GetPagedInvoicesQuery, ErrorOr<InvoiceListResponseDto>>
{
    public async Task<ErrorOr<InvoiceListResponseDto>> Handle(
        GetPagedInvoicesQuery request,
        CancellationToken cancellationToken)
    {
        if (!settings.Value.Enabled)
            return Errors.Invoice.SapDisabled;

        if (request.Page < 1)
            return Errors.Invoice.InvalidPage;

        var hasFilters = request.DocNum.HasValue || !string.IsNullOrEmpty(request.CardCode) || request.FromDate.HasValue || request.ToDate.HasValue;
        var maxPageSize = hasFilters ? 5000 : 100;

        if (request.PageSize < 1 || request.PageSize > maxPageSize)
            return Errors.Invoice.InvalidPageSize(maxPageSize);

        try
        {
            var skip = (request.Page - 1) * request.PageSize;
            var invoices = await sapClient.GetPagedInvoicesByOffsetAsync(skip, request.PageSize, request.DocNum, request.CardCode, request.FromDate, request.ToDate, cancellationToken);
            var totalCount = await sapClient.GetInvoicesCountAsync(request.DocNum, request.CardCode, request.FromDate, request.ToDate, cancellationToken);

            logger.LogInformation("Retrieved page {Page} of invoices ({Count} records, total: {Total})", request.Page, invoices.Count, totalCount);

            return new InvoiceListResponseDto
            {
                Page = request.Page,
                PageSize = request.PageSize,
                Count = invoices.Count,
                TotalCount = totalCount,
                TotalPages = (int)Math.Ceiling(totalCount / (double)request.PageSize),
                HasMore = invoices.Count == request.PageSize,
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
            logger.LogError(ex, "Error retrieving paged invoices");
            return Errors.Invoice.CreationFailed(ex.Message);
        }
    }
}
