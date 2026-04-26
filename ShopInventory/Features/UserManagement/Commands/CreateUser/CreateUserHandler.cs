using ErrorOr;
using System.Security.Claims;
using System.Text.Json;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.UserManagement.Commands.CreateUser;

public sealed class CreateUserHandler(
    ApplicationDbContext context,
    IHttpContextAccessor httpContextAccessor,
    IAuditService auditService,
    ILogger<CreateUserHandler> logger
) : IRequestHandler<CreateUserCommand, ErrorOr<UserDetailDto>>
{
    public async Task<ErrorOr<UserDetailDto>> Handle(
        CreateUserCommand command,
        CancellationToken cancellationToken)
    {
        var request = command.Request;

        var currentUserIdClaim = httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(currentUserIdClaim, out var currentUserId))
        {
            return Errors.UserManagement.Unauthenticated;
        }

        var currentUser = await context.Users
            .AsNoTracking()
            .Where(user => user.Id == currentUserId)
            .Select(user => new { user.Role, user.IsActive })
            .FirstOrDefaultAsync(cancellationToken);

        if (currentUser is null || !currentUser.IsActive)
        {
            return Errors.UserManagement.Unauthenticated;
        }

        if (string.Equals(currentUser.Role, "SalesRep", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(request.Role, "Merchandiser", StringComparison.OrdinalIgnoreCase))
            {
                return Errors.UserManagement.SalesRepCanOnlyCreateMerchandisers;
            }

            if (request.Permissions is { Count: > 0 })
            {
                return Errors.UserManagement.SalesRepCannotAssignCustomPermissions;
            }
        }

        if (await context.Users.AnyAsync(user => user.Username == request.Username, cancellationToken))
        {
            return Errors.UserManagement.CreationFailed("Username already exists");
        }

        if (await context.Users.AnyAsync(user => user.Email == request.Email, cancellationToken))
        {
            return Errors.UserManagement.CreationFailed("Email already exists");
        }

        var validRoles = new[]
        {
            "Admin", "Manager", "User", "ReadOnly", "Cashier", "StockController", "DepotController",
            "PodOperator", "Driver", "Merchandiser", "SalesRep"
        };

        if (!validRoles.Contains(request.Role, StringComparer.Ordinal))
        {
            return Errors.UserManagement.CreationFailed($"Invalid role. Valid roles: {string.Join(", ", validRoles)}");
        }

        if ((request.Role == "StockController" || request.Role == "DepotController") &&
            (request.AssignedWarehouseCodes == null || request.AssignedWarehouseCodes.Count == 0))
        {
            return Errors.UserManagement.CreationFailed($"At least one assigned warehouse code is required for {request.Role} role");
        }

        if (request.Role == "Merchandiser" && (request.AssignedCustomerCodes == null || request.AssignedCustomerCodes.Count == 0))
        {
            return Errors.UserManagement.CreationFailed("At least one assigned customer code is required for Merchandiser role");
        }

        if (request.Role == "Driver" && string.IsNullOrWhiteSpace(request.AssignedSection))
        {
            return Errors.UserManagement.CreationFailed("An assigned section is required for Driver role");
        }

        List<string> permissions;
        if (request.Permissions is { Count: > 0 })
        {
            var allPermissions = Permission.GetAllPermissions();
            var invalidPermissions = request.Permissions.Except(allPermissions).ToList();
            if (invalidPermissions.Count > 0)
            {
                return Errors.UserManagement.CreationFailed($"Invalid permissions: {string.Join(", ", invalidPermissions)}");
            }

            permissions = request.Permissions;
        }
        else
        {
            permissions = Permission.GetDefaultPermissionsForRole(request.Role);
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = request.Username,
            Email = request.Email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            FirstName = request.FirstName,
            LastName = request.LastName,
            Role = request.Role,
            IsActive = true,
            EmailVerified = false,
            TwoFactorEnabled = false,
            Permissions = JsonSerializer.Serialize(permissions),
            CreatedAt = DateTime.UtcNow
        };

        if (request.Role == "StockController" || request.Role == "DepotController" || request.Role == "Merchandiser")
        {
            user.SetWarehouseCodes(request.AssignedWarehouseCodes);
        }

        if (request.Role == "Merchandiser")
        {
            user.SetCustomerCodes(request.AssignedCustomerCodes);
        }

        if (request.Role == "Driver")
        {
            user.AssignedSection = request.AssignedSection;
        }

        if (request.AllowedPaymentMethods is { Count: > 0 })
        {
            user.SetAllowedPaymentMethods(request.AllowedPaymentMethods);
        }

        if (!string.IsNullOrWhiteSpace(request.DefaultGLAccount))
        {
            user.DefaultGLAccount = request.DefaultGLAccount;
        }

        if (request.AllowedPaymentBusinessPartners is { Count: > 0 })
        {
            user.SetAllowedPaymentBusinessPartners(request.AllowedPaymentBusinessPartners);
        }

        context.Users.Add(user);
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "User {Username} created by {CreatorRole}",
            user.Username,
            currentUser.Role);

        try
        {
            await auditService.LogAsync(
                AuditActions.CreateUser,
                "User",
                user.Id.ToString(),
                $"User {request.Username} created with role {request.Role}",
                true);
        }
        catch
        {
        }

        return MapToUserDetailDto(user);
    }

    private static UserDetailDto MapToUserDetailDto(User user)
    {
        var permissions = new List<string>();
        if (!string.IsNullOrEmpty(user.Permissions))
        {
            try
            {
                permissions = JsonSerializer.Deserialize<List<string>>(user.Permissions) ?? new List<string>();
            }
            catch
            {
            }
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
            Permissions = permissions,
            AssignedWarehouseCodes = user.GetWarehouseCodes(),
            AllowedPaymentMethods = user.GetAllowedPaymentMethods(),
            DefaultGLAccount = user.DefaultGLAccount,
            AllowedPaymentBusinessPartners = user.GetAllowedPaymentBusinessPartners(),
            AssignedSection = user.AssignedSection,
            AssignedCustomerCodes = user.GetCustomerCodes(),
            CreatedAt = user.CreatedAt,
            UpdatedAt = user.UpdatedAt,
            LastLoginAt = user.LastLoginAt
        };
    }
}
