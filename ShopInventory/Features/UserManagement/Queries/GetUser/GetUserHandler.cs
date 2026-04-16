using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.UserManagement.Queries.GetUser;

public sealed class GetUserHandler(
    IUserManagementService userManagementService
) : IRequestHandler<GetUserQuery, ErrorOr<UserDetailDto>>
{
    public async Task<ErrorOr<UserDetailDto>> Handle(
        GetUserQuery query,
        CancellationToken cancellationToken)
    {
        var user = await userManagementService.GetUserByIdAsync(query.Id);
        if (user is null)
        {
            return Errors.UserManagement.NotFound(query.Id);
        }
        return user;
    }
}
