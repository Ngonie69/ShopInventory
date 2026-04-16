using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Merchandiser.Commands.SubmitMobileOrder;

public sealed record SubmitMobileOrderCommand(
    MerchandiserOrderRequest Request,
    Guid UserId
) : IRequest<ErrorOr<SalesOrderDto>>;
