using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.CustomerDocuments.Queries.CheckWhatsAppNumber;

/// <summary>
/// What is known about a number before a document is sent to it: whether it is one, whether its owner
/// asked not to receive documents, and whether it is already saved on the customer. Asks nothing of
/// WhatsApp — the delivery job checks the number before it sends.
/// </summary>
public sealed record CheckWhatsAppNumberQuery(
    string Phone,
    string? CardCode,
    int? RouteCustomerId) : IRequest<ErrorOr<WhatsAppNumberCheckResultDto>>;
