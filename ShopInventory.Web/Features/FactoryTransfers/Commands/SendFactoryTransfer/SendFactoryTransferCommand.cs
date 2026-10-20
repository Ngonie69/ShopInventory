using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.FactoryTransfers.Commands.SendFactoryTransfer;

public sealed record SendFactoryTransferCommand(SendFactoryTransferRequestDto Request) : IRequest<ErrorOr<FactoryTransferActionResultDto>>;
