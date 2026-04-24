using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.PurchaseInvoices.Queries.GetPurchaseInvoices;

public sealed class GetPurchaseInvoicesHandler(
    IPurchaseInvoiceService purchaseInvoiceService,
    ILogger<GetPurchaseInvoicesHandler> logger
) : IRequestHandler<GetPurchaseInvoicesQuery, ErrorOr<PurchaseInvoiceListResponse>>
{
    public async Task<ErrorOr<PurchaseInvoiceListResponse>> Handle(
        GetPurchaseInvoicesQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await purchaseInvoiceService.GetPurchaseInvoicesAsync(
                request.Page,
                request.PageSize,
                request.CardCode,
                request.FromDate,
                request.ToDate,
                cancellationToken);

            if (response is null)
            {
                return Errors.PurchaseInvoice.LoadInvoicesFailed("Failed to load purchase invoices.");
            }

            return response;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading purchase invoices in web CQRS handler");
            return Errors.PurchaseInvoice.LoadInvoicesFailed("Failed to load purchase invoices.");
        }
    }
}