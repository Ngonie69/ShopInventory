using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.CustomerDocuments.Queries.GetCustomerDocumentDeliveryStatus;

/// <summary>Where sending customer documents stands, for the WhatsApp Deliveries page.</summary>
public sealed record GetCustomerDocumentDeliveryStatusQuery : IRequest<ErrorOr<CustomerDocumentDeliveryStatusDto>>;
