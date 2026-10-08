using ErrorOr;
using MediatR;

namespace ShopInventory.Features.CustomerDocuments.Commands.RemoveCustomerWhatsAppContact;

/// <summary>
/// Takes a number off one customer — a wrong number, a staff member who left. Unlike an opt-out it
/// says nothing about the number itself, so it does not follow it to other customers.
/// </summary>
public sealed record RemoveCustomerWhatsAppContactCommand(
    int ContactId,
    Guid UserId) : IRequest<ErrorOr<Success>>;
