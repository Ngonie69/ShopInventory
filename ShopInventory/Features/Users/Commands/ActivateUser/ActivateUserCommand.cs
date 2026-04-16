using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Users.Commands.ActivateUser;

public sealed record ActivateUserCommand(Guid Id) : IRequest<ErrorOr<Success>>;
