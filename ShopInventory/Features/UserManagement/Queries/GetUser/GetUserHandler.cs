using ErrorOr;
using MediatR;
using Microsoft.AspNetCore.Http;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Security;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.UserManagement.Queries.GetUser;

public sealed class GetUserHandler(
    IHttpContextAccessor httpContextAccessor,
    ICallerAccountReader callerAccounts,
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

        var caller = await callerAccounts.ReadAsync(httpContextAccessor.HttpContext?.User, cancellationToken);
        if (caller.IsError)
        {
            return caller.Errors;
        }

        if (caller.Value.IsInRole(ApplicationRoles.PodOperator) &&
            !string.Equals(user.Role, ApplicationRoles.Driver, StringComparison.OrdinalIgnoreCase))
        {
            return Errors.UserManagement.PodOperatorCanOnlyManageDrivers;
        }

        return user;
    }
}
