using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.UserManagement.Queries.GetCurrentUser;

public sealed class GetCurrentUserHandler(
    IUserManagementService userManagementService
) : IRequestHandler<GetCurrentUserQuery, ErrorOr<UserDetailDto>>
{
    public async Task<ErrorOr<UserDetailDto>> Handle(
        GetCurrentUserQuery query,
        CancellationToken cancellationToken)
    {
        var user = await userManagementService.GetUserByIdAsync(query.UserId);
        if (user is null)
        {
            return Errors.UserManagement.NotFound(query.UserId);
        }
        return user;
    }
}
