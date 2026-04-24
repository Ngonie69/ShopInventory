using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.PurchaseInvoices.Queries.GetPurchaseInvoices;

public sealed class GetPurchaseInvoicesHandler(
    ISAPServiceLayerClient sapClient,
    ILogger<GetPurchaseInvoicesHandler> logger
) : IRequestHandler<GetPurchaseInvoicesQuery, ErrorOr<PurchaseInvoiceListResponseDto>>
{
    public async Task<ErrorOr<PurchaseInvoiceListResponseDto>> Handle(
        GetPurchaseInvoicesQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            List<SAPPurchaseInvoice> invoices;
            int totalCount;

            if (request.FromDate.HasValue && request.ToDate.HasValue)
            {
                invoices = await sapClient.GetPurchaseInvoicesByDateRangeAsync(request.FromDate.Value, request.ToDate.Value, cancellationToken);
            }
            else if (!string.IsNullOrWhiteSpace(request.CardCode))
            {
                invoices = await sapClient.GetPurchaseInvoicesBySupplierAsync(request.CardCode, cancellationToken);
            }
            else
            {
                invoices = await sapClient.GetPagedPurchaseInvoicesAsync(request.Page, request.PageSize, cancellationToken);
                totalCount = await sapClient.GetPurchaseInvoicesCountAsync(request.CardCode, request.FromDate, request.ToDate, cancellationToken);

                return new PurchaseInvoiceListResponseDto
                {
                    Page = request.Page,
                    PageSize = request.PageSize,
                    Count = invoices.Count,
                    TotalCount = totalCount,
                    TotalPages = (int)Math.Ceiling(totalCount / (double)request.PageSize),
                    HasMore = request.Page * request.PageSize < totalCount,
                    Invoices = invoices.Select(PurchaseInvoiceMappings.MapFromSap).ToList()
                };
            }

            if (!string.IsNullOrWhiteSpace(request.CardCode))
            {
                invoices = invoices
                    .Where(invoice => string.Equals(invoice.CardCode, request.CardCode, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            totalCount = invoices.Count;
            invoices = invoices
                .Skip((request.Page - 1) * request.PageSize)
                .Take(request.PageSize)
                .ToList();

            return new PurchaseInvoiceListResponseDto
            {
                Page = request.Page,
                PageSize = request.PageSize,
                Count = invoices.Count,
                TotalCount = totalCount,
                TotalPages = (int)Math.Ceiling(totalCount / (double)request.PageSize),
                HasMore = request.Page * request.PageSize < totalCount,
                Invoices = invoices.Select(PurchaseInvoiceMappings.MapFromSap).ToList()
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching purchase invoices from SAP");
            return Errors.PurchaseInvoice.LoadFailed(ex.Message);
        }
    }
}