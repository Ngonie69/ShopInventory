using ErrorOr;
using MediatR;

namespace ShopInventory.Features.UserManagement.Commands.UnlockUser;

public sealed record UnlockUserCommand(Guid Id) : IRequest<ErrorOr<Success>>;
