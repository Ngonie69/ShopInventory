using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.FactoryTransfers.Queries.GetDepotOrders;

public sealed record GetDepotOrdersQuery(string DepotCode) : IRequest<ErrorOr<List<DepotOrderDto>>>;
