using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Users.Queries.GetUsers;

public sealed class GetUsersHandler(
    ApplicationDbContext context
) : IRequestHandler<GetUsersQuery, ErrorOr<UserListResponseDto>>
{
    public async Task<ErrorOr<UserListResponseDto>> Handle(
        GetUsersQuery query,
        CancellationToken cancellationToken)
    {
        var dbQuery = context.Users.AsQueryable();

        if (!string.IsNullOrEmpty(query.Search))
        {
            dbQuery = dbQuery.Where(u => u.Username.Contains(query.Search) || (u.Email != null && u.Email.Contains(query.Search)));
        }

        if (!string.IsNullOrEmpty(query.Role))
        {
            dbQuery = dbQuery.Where(u => u.Role == query.Role);
        }

        var totalCount = await dbQuery.CountAsync(cancellationToken);

        var users = await dbQuery
            .OrderBy(u => u.Username)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(u => new UserDto
            {
                Id = u.Id,
                Username = u.Username,
                Email = u.Email,
                Role = u.Role,
                FirstName = u.FirstName,
                LastName = u.LastName,
                IsActive = u.IsActive,
                EmailVerified = u.EmailVerified,
                FailedLoginAttempts = u.FailedLoginAttempts,
                LockoutEnd = u.LockoutEnd,
                CreatedAt = u.CreatedAt,
                LastLoginAt = u.LastLoginAt,
                AssignedWarehouseCodes = u.GetWarehouseCodes(),
                AssignedSection = u.AssignedSection
            })
            .ToListAsync(cancellationToken);

        return new UserListResponseDto
        {
            TotalCount = totalCount,
            Page = query.Page,
            PageSize = query.PageSize,
            Users = users
        };
    }
}
