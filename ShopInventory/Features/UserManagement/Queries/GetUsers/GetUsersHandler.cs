using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.UserManagement.Queries.GetUsers;

public sealed class GetUsersHandler(
    ApplicationDbContext context
) : IRequestHandler<GetUsersQuery, ErrorOr<PagedResult<UserDetailDto>>>
{
    public async Task<ErrorOr<PagedResult<UserDetailDto>>> Handle(
        GetUsersQuery query,
        CancellationToken cancellationToken)
    {
        var page = Math.Max(query.Page, 1);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);

        var usersQuery = context.Users.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var pattern = $"%{query.Search.Trim()}%";
            usersQuery = usersQuery.Where(userEntity =>
                EF.Functions.ILike(userEntity.Username, pattern) ||
                (userEntity.Email != null && EF.Functions.ILike(userEntity.Email, pattern)) ||
                (userEntity.FirstName != null && EF.Functions.ILike(userEntity.FirstName, pattern)) ||
                (userEntity.LastName != null && EF.Functions.ILike(userEntity.LastName, pattern)));
        }

        if (!string.IsNullOrWhiteSpace(query.Role))
        {
            usersQuery = usersQuery.Where(userEntity => userEntity.Role == query.Role);
        }

        if (query.IsActive.HasValue)
        {
            usersQuery = usersQuery.Where(userEntity => userEntity.IsActive == query.IsActive.Value);
        }

        var totalCount = await usersQuery.CountAsync(cancellationToken);

        var userEntities = await usersQuery
            .OrderBy(userEntity => userEntity.Username)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var usernames = userEntities
            .Select(userEntity => userEntity.Username)
            .ToArray();

        var auditLoginRows = await context.AuditLogs
            .AsNoTracking()
            .Where(auditLog =>
                auditLog.IsSuccess &&
                (auditLog.Action == AuditActions.Login || auditLog.Action == AuditActions.PasskeyLogin) &&
                usernames.Contains(auditLog.Username))
            .GroupBy(auditLog => auditLog.Username)
            .Select(group => new
            {
                Username = group.Key,
                LastLoginAt = group.Max(auditLog => auditLog.Timestamp)
            })
            .ToListAsync(cancellationToken);

        var auditLoginTimesByUsername = auditLoginRows.ToDictionary(
            auditLogin => auditLogin.Username,
            auditLogin => (DateTime?)auditLogin.LastLoginAt,
            StringComparer.OrdinalIgnoreCase);

        var users = userEntities
            .Select(userEntity => MapToUserDetailDto(
                userEntity,
                auditLoginTimesByUsername.GetValueOrDefault(userEntity.Username)))
            .ToList();

        return new PagedResult<UserDetailDto>
        {
            Items = users,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    private static UserDetailDto MapToUserDetailDto(User user, DateTime? auditLastLoginAt)
    {
        var lastLoginAt = user.LastLoginAt;
        if (auditLastLoginAt.HasValue && (!lastLoginAt.HasValue || auditLastLoginAt.Value > lastLoginAt.Value))
        {
            lastLoginAt = auditLastLoginAt;
        }

        return new UserDetailDto
        {
            Id = user.Id,
            Username = user.Username,
            Email = user.Email,
            FirstName = user.FirstName,
            LastName = user.LastName,
            Role = user.Role,
            IsActive = user.IsActive,
            EmailVerified = user.EmailVerified,
            TwoFactorEnabled = user.TwoFactorEnabled,
            IsLockedOut = user.LockoutEnd.HasValue && user.LockoutEnd > DateTime.UtcNow,
            LockoutEnd = user.LockoutEnd,
            Permissions = DeserializeStringList(user.Permissions),
            AssignedWarehouseCodes = user.GetWarehouseCodes(),
            AllowedPaymentMethods = user.GetAllowedPaymentMethods(),
            DefaultGLAccount = user.DefaultGLAccount,
            AllowedPaymentBusinessPartners = user.GetAllowedPaymentBusinessPartners(),
            AssignedSection = user.AssignedSection,
            AssignedCustomerCodes = user.GetCustomerCodes(),
            CreatedAt = user.CreatedAt,
            UpdatedAt = user.UpdatedAt,
            LastLoginAt = lastLoginAt
        };
    }

    private static List<string> DeserializeStringList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new List<string>();
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }
}
