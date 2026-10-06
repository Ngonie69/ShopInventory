using ShopInventory.DTOs;
using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DesktopIntegration.Commands.ConvertSalesOrderToInvoice;

/// <param name="SignBeforeAnswering">
/// Whether the invoice is fiscalised in the request, before the caller is answered, and handed to the queue
/// already signed — the way a direct van sale is. Set by the van route, whose rep is at the counter waiting
/// for the receipt to print. Other callers leave it off and the queue signs the invoice later.
/// </param>
public sealed record ConvertSalesOrderToInvoiceCommand(
    ConvertSalesOrderToInvoiceRequest Request,
    string? CreatedBy,
    bool SignBeforeAnswering = false
) : IRequest<ErrorOr<ConvertSalesOrderToInvoiceResponseDto>>;
