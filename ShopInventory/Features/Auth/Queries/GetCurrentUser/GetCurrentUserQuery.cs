using ErrorOr;
using MediatR;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Auth.Queries.GetCurrentUser;

public sealed record GetCurrentUserQuery(
    string Username
) : IRequest<ErrorOr<UserInfo>>;
