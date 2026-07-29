using ErrorOr;
using MediatR;
using Microsoft.AspNetCore.Http;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.UserManagement.Queries.GetUserPermissions;

public sealed class GetUserPermissionsHandler(
    IHttpContextAccessor httpContextAccessor,
    IUserManagementService userManagementService
) : IRequestHandler<GetUserPermissionsQuery, ErrorOr<UserPermissionsResponse>>
{
    public async Task<ErrorOr<UserPermissionsResponse>> Handle(
        GetUserPermissionsQuery query,
        CancellationToken cancellationToken)
    {
        var permissions = await userManagementService.GetUserPermissionsAsync(query.Id);
        if (permissions is null)
        {
            return Errors.UserManagement.NotFound(query.Id);
        }

        if (httpContextAccessor.HttpContext?.User.IsInRole(ApplicationRoles.PodOperator) == true &&
            !string.Equals(permissions.Role, ApplicationRoles.Driver, StringComparison.OrdinalIgnoreCase))
        {
            return Errors.UserManagement.PodOperatorCanOnlyManageDrivers;
        }

        return permissions;
    }
}
