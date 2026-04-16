using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.UserManagement.Queries.GetUserPermissions;

public sealed class GetUserPermissionsHandler(
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
        return permissions;
    }
}
