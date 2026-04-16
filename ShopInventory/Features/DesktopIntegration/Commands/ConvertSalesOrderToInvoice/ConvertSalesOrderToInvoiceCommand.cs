using ShopInventory.DTOs;
using ErrorOr;
using MediatR;

namespace ShopInventory.Features.DesktopIntegration.Commands.ConvertSalesOrderToInvoice;

public sealed record ConvertSalesOrderToInvoiceCommand(
    ConvertSalesOrderToInvoiceRequest Request,
    string? CreatedBy
) : IRequest<ErrorOr<ConvertSalesOrderToInvoiceResponseDto>>;
