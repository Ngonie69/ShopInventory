using ErrorOr;
using MediatR;

namespace ShopInventory.Features.UserManagement.Commands.DeleteUser;

public sealed record DeleteUserCommand(Guid Id) : IRequest<ErrorOr<Success>>;
