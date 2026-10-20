using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.FactoryTransfers.Commands.RetryFactoryTransfer;

public sealed record RetryFactoryTransferCommand(int Id) : IRequest<ErrorOr<FactoryTransferActionResultDto>>;
