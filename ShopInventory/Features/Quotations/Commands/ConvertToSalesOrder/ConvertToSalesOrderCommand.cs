using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Quotations.Commands.ConvertToSalesOrder;

public sealed record ConvertToSalesOrderCommand(
    int Id,
    Guid UserId
) : IRequest<ErrorOr<SalesOrderDto>>;
