using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.PurchaseQuotations.Queries.GetPurchaseQuotations;

public sealed class GetPurchaseQuotationsHandler(
    IPurchaseQuotationService purchaseQuotationService,
    ILogger<GetPurchaseQuotationsHandler> logger
) : IRequestHandler<GetPurchaseQuotationsQuery, ErrorOr<PurchaseQuotationListResponse>>
{
    public async Task<ErrorOr<PurchaseQuotationListResponse>> Handle(
        GetPurchaseQuotationsQuery request,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await purchaseQuotationService.GetPurchaseQuotationsAsync(
                request.Page,
                request.PageSize,
                request.CardCode,
                request.FromDate,
                request.ToDate,
                cancellationToken);

            if (response is null)
                return Errors.PurchaseQuotation.LoadQuotationsFailed("Failed to load purchase quotations.");

            return response;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading purchase quotations in web CQRS handler");
            return Errors.PurchaseQuotation.LoadQuotationsFailed("Failed to load purchase quotations.");
        }
    }
}