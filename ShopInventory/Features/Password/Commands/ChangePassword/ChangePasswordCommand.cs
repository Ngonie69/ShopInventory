using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Password.Commands.ChangePassword;

/// <summary>
/// The signed-in user changing their own password. The user is the token's subject and nothing
/// else: there is deliberately no username here, because a caller who could name the account could
/// change any account whose password they could guess.
/// </summary>
public sealed record ChangePasswordCommand(
    Guid UserId,
    string CurrentPassword,
    string NewPassword,
    string ConfirmPassword
) : IRequest<ErrorOr<string>>;
