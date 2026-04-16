using ErrorOr;
using MediatR;

namespace ShopInventory.Features.UserManagement.Commands.ResetTwoFactor;

public sealed record ResetTwoFactorCommand(Guid Id) : IRequest<ErrorOr<Success>>;
