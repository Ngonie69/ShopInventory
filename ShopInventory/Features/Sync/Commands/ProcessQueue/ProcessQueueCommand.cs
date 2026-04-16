using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Sync.Commands.ProcessQueue;

public sealed record ProcessQueueCommand() : IRequest<ErrorOr<Success>>;
