using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.DesktopIntegration.Commands.CreateQueuedInvoice;

public sealed record CreateQueuedInvoiceCommand(
    CreateDesktopInvoiceRequest Request,
    string? CreatedBy
) : IRequest<ErrorOr<QueuedInvoiceResponseDto>>;
