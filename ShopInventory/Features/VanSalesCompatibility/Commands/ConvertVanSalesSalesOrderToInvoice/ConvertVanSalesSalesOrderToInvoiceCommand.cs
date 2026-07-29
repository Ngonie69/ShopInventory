using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.VanSalesCompatibility.Commands.ConvertVanSalesSalesOrderToInvoice;

public sealed record ConvertVanSalesSalesOrderToInvoiceCommand(
    VanSalesOrderRequest Request,
    Guid UserId
) : IRequest<ErrorOr<VanSalesConvertSalesOrderToInvoiceResponse>>;