using ErrorOr;
using MediatR;
using Microsoft.AspNetCore.Http;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.UserManagement.Queries.GetUser;

public sealed class GetUserHandler(
    IHttpContextAccessor httpContextAccessor,
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

        if (httpContextAccessor.HttpContext?.User.IsInRole("PodOperator") == true &&
            !string.Equals(user.Role, "Driver", StringComparison.OrdinalIgnoreCase))
        {
            return Errors.UserManagement.PodOperatorCanOnlyManageDrivers;
        }

        return user;
    }
}
