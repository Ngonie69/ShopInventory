using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Users.Commands.RequirePasswordChange;

/// <summary>
/// Makes an account's holder choose a new password before using it again.
/// </summary>
/// <remarks>
/// For a password that may be known to someone it should not be. It does not set a new password —
/// nobody but the holder should ever know the next one — it ends the account's sessions and holds
/// it to the change-password screen from the next sign-in.
/// </remarks>
public sealed record RequirePasswordChangeCommand(Guid Id) : IRequest<ErrorOr<Success>>;
