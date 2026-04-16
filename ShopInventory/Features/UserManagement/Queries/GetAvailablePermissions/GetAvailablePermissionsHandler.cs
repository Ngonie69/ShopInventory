using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.UserManagement.Queries.GetAvailablePermissions;

public sealed class GetAvailablePermissionsHandler(
    IUserManagementService userManagementService
) : IRequestHandler<GetAvailablePermissionsQuery, ErrorOr<AvailablePermissionsResponse>>
{
    public Task<ErrorOr<AvailablePermissionsResponse>> Handle(
        GetAvailablePermissionsQuery query,
        CancellationToken cancellationToken)
    {
        var permissions = userManagementService.GetAvailablePermissions();
        return Task.FromResult<ErrorOr<AvailablePermissionsResponse>>(permissions);
    }
}
