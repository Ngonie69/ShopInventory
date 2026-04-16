using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Password.Commands.ChangePassword;

public sealed record ChangePasswordCommand(
    Guid? UserId,
    string? Username,
    string CurrentPassword,
    string NewPassword,
    string ConfirmPassword
) : IRequest<ErrorOr<string>>;
