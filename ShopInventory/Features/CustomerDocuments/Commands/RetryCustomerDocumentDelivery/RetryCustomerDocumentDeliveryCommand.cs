using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.CustomerDocuments.Commands.RetryCustomerDocumentDelivery;

/// <summary>Sends a document again, as a new delivery that records which one it replaces.</summary>
public sealed record RetryCustomerDocumentDeliveryCommand(
    long DeliveryId,
    RetryCustomerDocumentDeliveryRequest Request,
    Guid UserId) : IRequest<ErrorOr<CustomerDocumentDeliveryDto>>;
