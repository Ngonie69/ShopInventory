using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.CustomerDocuments.Commands.UpdateCustomerWhatsAppContact;

/// <summary>Changes a saved number's contact name and whether it receives invoices on its own.</summary>
public sealed record UpdateCustomerWhatsAppContactCommand(
    int ContactId,
    UpdateCustomerWhatsAppContactRequest Request,
    Guid UserId) : IRequest<ErrorOr<CustomerWhatsAppContactDto>>;
