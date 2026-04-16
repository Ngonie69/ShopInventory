using ErrorOr;
using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.UserManagement.Queries.GetUsers;

public sealed class GetUsersHandler(
    IUserManagementService userManagementService
) : IRequestHandler<GetUsersQuery, ErrorOr<PagedResult<UserDetailDto>>>
{
    public async Task<ErrorOr<PagedResult<UserDetailDto>>> Handle(
        GetUsersQuery query,
        CancellationToken cancellationToken)
    {
        var result = await userManagementService.GetUsersAsync(query.Page, query.PageSize, query.Search, query.Role, query.IsActive);
        return result;
    }
}
