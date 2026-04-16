using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.DesktopIntegration.Commands.CreateInvoiceDirect;

public sealed record CreateInvoiceDirectCommand(
    CreateDesktopInvoiceRequest Request,
    string? CreatedBy
) : IRequest<ErrorOr<ConfirmReservationResponseDto>>;
