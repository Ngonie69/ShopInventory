using ErrorOr;
using MediatR;
using ShopInventory.Common.Auth;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Auth.Queries.GetCurrentUser;

public sealed class GetCurrentUserHandler(
    IAuthService authService,
    ApplicationDbContext context,
    ILogger<GetCurrentUserHandler> logger
) : IRequestHandler<GetCurrentUserQuery, ErrorOr<UserInfo>>
{
    public async Task<ErrorOr<UserInfo>> Handle(
        GetCurrentUserQuery query,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(query.Username))
        {
            return Errors.Auth.Unauthenticated;
        }

        var user = await authService.GetUserByUsernameAsync(query.Username);

        if (user is null)
        {
            logger.LogWarning("User not found: {Username}", query.Username);
            return Errors.Auth.UserNotFound;
        }

        return await UserInfoMapper.FromUserAsync(user, context, cancellationToken);
    }
}
