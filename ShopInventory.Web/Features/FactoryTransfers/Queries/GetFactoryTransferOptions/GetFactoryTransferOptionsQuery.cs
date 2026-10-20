using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.FactoryTransfers.Queries.GetFactoryTransferOptions;

public sealed record GetFactoryTransferOptionsQuery() : IRequest<ErrorOr<FactoryTransferOptionsDto>>;
