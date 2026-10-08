using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.CustomerDocuments.Commands.CancelCustomerDocumentDelivery;

/// <summary>Withdraws a document that has not been handed to WhatsApp yet.</summary>
public sealed record CancelCustomerDocumentDeliveryCommand(
    long DeliveryId,
    Guid UserId) : IRequest<ErrorOr<CustomerDocumentDeliveryDto>>;
