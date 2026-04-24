using ErrorOr;
using MediatR;
using ShopInventory.Web.Common.Errors;
using ShopInventory.Web.Models;
using ShopInventory.Web.Services;

namespace ShopInventory.Web.Features.PurchaseInvoices.Commands.CreatePurchaseInvoice;

public sealed class CreatePurchaseInvoiceHandler(
    IPurchaseInvoiceService purchaseInvoiceService,
    ILogger<CreatePurchaseInvoiceHandler> logger
) : IRequestHandler<CreatePurchaseInvoiceCommand, ErrorOr<PurchaseInvoiceDto>>
{
    public async Task<ErrorOr<PurchaseInvoiceDto>> Handle(
        CreatePurchaseInvoiceCommand request,
        CancellationToken cancellationToken)
    {
        try
        {
            var createdInvoice = await purchaseInvoiceService.CreatePurchaseInvoiceAsync(request.Request, cancellationToken);

            if (createdInvoice is null)
            {
                return Errors.PurchaseInvoice.CreateInvoiceFailed("Failed to create purchase invoice.");
            }

            return createdInvoice;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Purchase invoice creation request failed");
            return Errors.PurchaseInvoice.CreateInvoiceFailed(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error creating purchase invoice");
            return Errors.PurchaseInvoice.CreateInvoiceFailed("Failed to create purchase invoice.");
        }
    }
}