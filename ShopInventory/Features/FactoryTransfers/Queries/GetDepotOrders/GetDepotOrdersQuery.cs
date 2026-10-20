using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.FactoryTransfers.Queries.GetDepotOrders;

public sealed record GetDepotOrdersQuery(Guid UserId, string DepotCode) : IRequest<ErrorOr<List<DepotOrderDto>>>;
