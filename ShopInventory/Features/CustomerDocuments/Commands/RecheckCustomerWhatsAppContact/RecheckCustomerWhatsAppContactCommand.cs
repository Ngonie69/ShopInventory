using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.CustomerDocuments.Commands.RecheckCustomerWhatsAppContact;

/// <summary>Asks WhatsApp now whether a saved number has an account.</summary>
public sealed record RecheckCustomerWhatsAppContactCommand(
    int ContactId,
    Guid UserId) : IRequest<ErrorOr<CustomerWhatsAppContactDto>>;
