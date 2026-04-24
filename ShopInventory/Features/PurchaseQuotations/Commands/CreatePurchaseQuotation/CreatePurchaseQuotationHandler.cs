using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Features.PurchaseQuotations;
using ShopInventory.Services;

namespace ShopInventory.Features.PurchaseQuotations.Commands.CreatePurchaseQuotation;

public sealed class CreatePurchaseQuotationHandler(
    ISAPServiceLayerClient sapClient,
    ILogger<CreatePurchaseQuotationHandler> logger
) : IRequestHandler<CreatePurchaseQuotationCommand, ErrorOr<PurchaseQuotationDto>>
{
    public async Task<ErrorOr<PurchaseQuotationDto>> Handle(
        CreatePurchaseQuotationCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var purchaseQuotation = await sapClient.CreatePurchaseQuotationAsync(command.Request, cancellationToken);
            return PurchaseQuotationMappings.MapFromSap(purchaseQuotation);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating purchase quotation for supplier {CardCode}", command.Request.CardCode);
            return Errors.PurchaseQuotation.CreationFailed(ex.Message);
        }
    }
}