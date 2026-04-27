using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Merchandiser.Queries.GetMobileOrderById;

public sealed record GetMobileOrderByIdQuery(
    Guid UserId,
    int Id
) : IRequest<ErrorOr<SalesOrderDto>>;