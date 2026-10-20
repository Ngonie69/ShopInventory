using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.FactoryTransfers.Commands.DecideFactoryTransfer;

public sealed record DecideFactoryTransferCommand(int Id, DecideFactoryTransferRequestDto Request) : IRequest<ErrorOr<FactoryTransferActionResultDto>>;
