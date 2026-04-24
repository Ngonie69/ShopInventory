using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.PurchaseQuotations.Commands.CreatePurchaseQuotation;

public sealed class CreatePurchaseQuotationHandler(
    IPurchaseQuotationService purchaseQuotationService,
    ILogger<CreatePurchaseQuotationHandler> logger
) : IRequestHandler<CreatePurchaseQuotationCommand, ErrorOr<PurchaseQuotationDto>>
{
    public async Task<ErrorOr<PurchaseQuotationDto>> Handle(
        CreatePurchaseQuotationCommand request,
        CancellationToken cancellationToken)
    {
        try
        {
            var createdQuotation = await purchaseQuotationService.CreatePurchaseQuotationAsync(request.Request, cancellationToken);

            if (createdQuotation is null)
                return Errors.PurchaseQuotation.CreateQuotationFailed("Failed to create purchase quotation.");

            return createdQuotation;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Purchase quotation creation request failed");
            return Errors.PurchaseQuotation.CreateQuotationFailed(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error creating purchase quotation");
            return Errors.PurchaseQuotation.CreateQuotationFailed("Failed to create purchase quotation.");
        }
    }
}