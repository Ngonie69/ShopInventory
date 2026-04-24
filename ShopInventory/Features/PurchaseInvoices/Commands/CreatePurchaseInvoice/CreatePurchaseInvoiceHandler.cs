using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Features.PurchaseInvoices;
using ShopInventory.Services;

namespace ShopInventory.Features.PurchaseInvoices.Commands.CreatePurchaseInvoice;

public sealed class CreatePurchaseInvoiceHandler(
    ISAPServiceLayerClient sapClient,
    ILogger<CreatePurchaseInvoiceHandler> logger
) : IRequestHandler<CreatePurchaseInvoiceCommand, ErrorOr<PurchaseInvoiceDto>>
{
    public async Task<ErrorOr<PurchaseInvoiceDto>> Handle(
        CreatePurchaseInvoiceCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            var invoice = await sapClient.CreatePurchaseInvoiceAsync(command.Request, cancellationToken);
            return PurchaseInvoiceMappings.MapFromSap(invoice);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating purchase invoice for supplier {CardCode}", command.Request.CardCode);
            return Errors.PurchaseInvoice.CreationFailed(ex.Message);
        }
    }
}