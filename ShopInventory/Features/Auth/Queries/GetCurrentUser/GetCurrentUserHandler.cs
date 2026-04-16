using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Auth.Queries.GetCurrentUser;

public sealed class GetCurrentUserHandler(
    IAuthService authService,
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

        return new UserInfo
        {
            Username = user.Username,
            Role = user.Role,
            Email = user.Email,
            AssignedWarehouseCode = user.AssignedWarehouseCode,
            AssignedWarehouseCodes = user.GetWarehouseCodes(),
            AllowedPaymentMethods = user.GetAllowedPaymentMethods(),
            AssignedCustomerCodes = user.GetCustomerCodes()
        };
    }
}
