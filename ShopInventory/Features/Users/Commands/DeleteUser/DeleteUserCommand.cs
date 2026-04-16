using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Users.Commands.DeleteUser;

public sealed record DeleteUserCommand(Guid Id) : IRequest<ErrorOr<Deleted>>;
