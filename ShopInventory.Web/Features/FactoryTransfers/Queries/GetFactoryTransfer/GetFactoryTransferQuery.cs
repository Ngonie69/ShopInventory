using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.FactoryTransfers.Queries.GetFactoryTransfer;

public sealed record GetFactoryTransferQuery(int Id) : IRequest<ErrorOr<FactoryTransferDetailDto>>;
