using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Users.Commands.DeactivateUser;

public sealed record DeactivateUserCommand(Guid Id) : IRequest<ErrorOr<Success>>;
