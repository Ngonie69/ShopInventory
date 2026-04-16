using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Email.Commands.ProcessEmailQueue;

public sealed record ProcessEmailQueueCommand() : IRequest<ErrorOr<Success>>;
