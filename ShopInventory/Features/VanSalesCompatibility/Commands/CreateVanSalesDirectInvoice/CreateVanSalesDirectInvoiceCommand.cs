using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.VanSalesCompatibility.Commands.CreateVanSalesDirectInvoice;

public sealed record CreateVanSalesDirectInvoiceCommand(
    VanSalesOrderRequest Request,
    Guid UserId
) : IRequest<ErrorOr<VanSalesDirectInvoiceResponse>>;