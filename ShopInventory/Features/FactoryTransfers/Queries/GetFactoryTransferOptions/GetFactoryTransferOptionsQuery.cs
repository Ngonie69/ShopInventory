using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.FactoryTransfers.Queries.GetFactoryTransferOptions;

public sealed record GetFactoryTransferOptionsQuery(Guid UserId) : IRequest<ErrorOr<FactoryTransferOptionsDto>>;
