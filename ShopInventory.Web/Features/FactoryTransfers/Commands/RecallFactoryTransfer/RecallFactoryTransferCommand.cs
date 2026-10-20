using ErrorOr;
using MediatR;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Features.FactoryTransfers.Commands.RecallFactoryTransfer;

public sealed record RecallFactoryTransferCommand(int Id, string Reason) : IRequest<ErrorOr<FactoryTransferActionResultDto>>;
