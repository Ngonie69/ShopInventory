using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.CustomerDocuments.Commands.UpdateCustomerDocumentDeliverySettings;

/// <summary>Sets which session sends customer documents, whether invoices go on their own, and the automatic cap.</summary>
public sealed record UpdateCustomerDocumentDeliverySettingsCommand(
    UpdateCustomerDocumentDeliverySettingsRequest Request,
    Guid UserId) : IRequest<ErrorOr<CustomerDocumentDeliveryStatusDto>>;
