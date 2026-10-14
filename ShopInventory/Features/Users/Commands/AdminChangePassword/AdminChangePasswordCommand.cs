using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Users.Commands.AdminChangePassword;

/// <param name="Id">The account whose password is being replaced.</param>
/// <param name="Request">The new password.</param>
/// <param name="ChosenByOwner">
/// True when the account's own holder typed it, as on the van app's change-password screen, which
/// reuses this command. False leaves the owner owing a change of their own at the next sign-in,
/// because a password an administrator typed is known to the administrator.
/// </param>
public sealed record AdminChangePasswordCommand(
    Guid Id,
    AdminChangePasswordRequest Request,
    bool ChosenByOwner = false
) : IRequest<ErrorOr<Success>>;
