using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.FactoryTransfers.Commands.CancelFactoryTransfer;

public sealed record CancelFactoryTransferCommand(int Id) : IRequest<ErrorOr<FactoryTransferActionResultDto>>;
