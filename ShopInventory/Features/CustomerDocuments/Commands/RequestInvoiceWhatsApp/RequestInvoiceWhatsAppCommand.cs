using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.CustomerDocuments.Commands.RequestInvoiceWhatsApp;

/// <summary>
/// Queues a SAP invoice to be sent to the customer's WhatsApp numbers, or to one typed for this send.
/// </summary>
/// <param name="DocEntry">The invoice.</param>
/// <param name="Request">Which numbers.</param>
/// <param name="UserId">Who asked. From the token: the send goes out in the company's name on their word.</param>
public sealed record RequestInvoiceWhatsAppCommand(
    int DocEntry,
    RequestInvoiceWhatsAppRequest Request,
    Guid UserId) : IRequest<ErrorOr<List<CustomerDocumentDeliveryDto>>>;
