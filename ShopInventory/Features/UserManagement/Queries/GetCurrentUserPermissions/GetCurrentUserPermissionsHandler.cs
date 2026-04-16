using ErrorOr;
using MediatR;
using ShopInventory.Services;

namespace ShopInventory.Features.UserManagement.Queries.GetCurrentUserPermissions;

public sealed class GetCurrentUserPermissionsHandler(
    IUserManagementService userManagementService
) : IRequestHandler<GetCurrentUserPermissionsQuery, ErrorOr<List<string>>>
{
    public async Task<ErrorOr<List<string>>> Handle(
        GetCurrentUserPermissionsQuery query,
        CancellationToken cancellationToken)
    {
        var permissions = await userManagementService.GetEffectivePermissionsAsync(query.UserId);
        return permissions;
    }
}
