using ErrorOr;
using MediatR;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Users.Queries.GetUser;

public sealed class GetUserHandler(
    ApplicationDbContext context
) : IRequestHandler<GetUserQuery, ErrorOr<UserDto>>
{
    public async Task<ErrorOr<UserDto>> Handle(
        GetUserQuery query,
        CancellationToken cancellationToken)
    {
        var user = await context.Users.FindAsync(new object[] { query.Id }, cancellationToken);

        if (user is null)
        {
            return Errors.User.NotFound(query.Id);
        }

        return new UserDto
        {
            Id = user.Id,
            Username = user.Username,
            Email = user.Email,
            Role = user.Role,
            FirstName = user.FirstName,
            LastName = user.LastName,
            IsActive = user.IsActive,
            EmailVerified = user.EmailVerified,
            FailedLoginAttempts = user.FailedLoginAttempts,
            LockoutEnd = user.LockoutEnd,
            CreatedAt = user.CreatedAt,
            LastLoginAt = user.LastLoginAt,
            AssignedWarehouseCodes = user.GetWarehouseCodes(),
            AssignedCustomerCodes = user.GetCustomerCodes(),
            AssignedSection = user.AssignedSection
        };
    }
}
