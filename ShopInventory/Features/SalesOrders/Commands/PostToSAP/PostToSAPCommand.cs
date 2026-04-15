using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.SalesOrders.Commands.PostToSAP;

public sealed record PostToSAPCommand(int Id, Guid UserId) : IRequest<ErrorOr<SalesOrderDto>>;
