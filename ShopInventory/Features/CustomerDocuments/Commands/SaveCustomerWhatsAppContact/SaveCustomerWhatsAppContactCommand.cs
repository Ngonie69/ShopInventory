using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.CustomerDocuments.Commands.SaveCustomerWhatsAppContact;

/// <summary>
/// Saves a WhatsApp number a customer gave for their documents, with their consent.
/// </summary>
/// <param name="Request">The number, whose it is, and how the customer agreed.</param>
/// <param name="UserId">Who recorded the consent. From the token: it is the consent's witness.</param>
public sealed record SaveCustomerWhatsAppContactCommand(
    SaveCustomerWhatsAppContactRequest Request,
    Guid UserId) : IRequest<ErrorOr<List<CustomerWhatsAppContactDto>>>;
