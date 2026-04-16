using ErrorOr;
using MediatR;

namespace ShopInventory.Features.Auth.Commands.Logout;

public sealed record LogoutCommand(
    string RefreshToken,
    string IpAddress
) : IRequest<ErrorOr<Deleted>>;
