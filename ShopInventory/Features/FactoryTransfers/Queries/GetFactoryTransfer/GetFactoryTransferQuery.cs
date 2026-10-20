using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.FactoryTransfers.Queries.GetFactoryTransfer;

public sealed record GetFactoryTransferQuery(int TransferId, Guid UserId) : IRequest<ErrorOr<FactoryTransferDetailDto>>;
