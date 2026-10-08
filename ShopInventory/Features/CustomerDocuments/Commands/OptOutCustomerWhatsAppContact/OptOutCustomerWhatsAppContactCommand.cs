using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.CustomerDocuments.Commands.OptOutCustomerWhatsAppContact;

/// <summary>
/// Stops sending documents to a number, on every customer it is saved on — the customer asked.
/// </summary>
public sealed record OptOutCustomerWhatsAppContactCommand(
    int ContactId,
    Guid UserId) : IRequest<ErrorOr<List<CustomerWhatsAppContactDto>>>;
