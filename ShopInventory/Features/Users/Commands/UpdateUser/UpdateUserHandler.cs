using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Extensions;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.Users.Commands.UpdateUser;

public sealed class UpdateUserHandler(
    ApplicationDbContext context,
    IAuditService auditService,
    ILogger<UpdateUserHandler> logger
) : IRequestHandler<UpdateUserCommand, ErrorOr<UserDto>>
{
    public async Task<ErrorOr<UserDto>> Handle(
        UpdateUserCommand command,
        CancellationToken cancellationToken)
    {
        var user = await context.Users.FindAsync(new object[] { command.Id }, cancellationToken);

        if (user is null)
        {
            return Errors.User.NotFound(command.Id);
        }

        var request = command.Request;

        if (!string.IsNullOrEmpty(request.Email) && !string.Equals(request.Email, user.Email, StringComparison.OrdinalIgnoreCase))
        {
            if (await context.Users
                .Where(u => u.Id != command.Id)
                .WhereUsernameOrEmailMatches(request.Email)
                .AnyAsync(cancellationToken))
            {
                return Errors.User.UpdateFailed("Email already exists");
            }
            user.Email = request.Email;
        }

        if (!string.IsNullOrEmpty(request.Role))
        {
            if (!ApplicationRoles.CanAssignOrRetainManagedRole(request.Role, user.Role))
            {
                return Errors.User.UpdateFailed($"Invalid role. Valid roles are: {ApplicationRoles.DescribeAssignableRoles()}");
            }
            user.Role = request.Role.Trim();
        }

        if (request.FirstName != null) user.FirstName = request.FirstName;
        if (request.LastName != null) user.LastName = request.LastName;
        if (request.IsActive.HasValue) user.IsActive = request.IsActive.Value;

        if (request.AssignedWarehouseCodes != null)
        {
            if (ApplicationRoles.SupportsWarehouseAssignments(user.Role))
                user.SetWarehouseCodes(request.AssignedWarehouseCodes);
            else
                user.SetWarehouseCodes(null);
        }

        if (request.AssignedCustomerCodes != null)
        {
            if (ApplicationRoles.SupportsCustomerAssignments(user.Role))
                user.SetCustomerCodes(request.AssignedCustomerCodes);
            else
                user.SetCustomerCodes(null);
        }

        if (ApplicationRoles.RequiresAssignedSection(user.Role))
            user.AssignedSection = request.AssignedSection;
        else
            user.AssignedSection = null;

        if (ApplicationRoles.RequiresWarehouseAssignments(user.Role) && user.GetWarehouseCodes().Count == 0)
        {
            return Errors.User.UpdateFailed($"At least one assigned warehouse code is required for {user.Role} role");
        }

        if (ApplicationRoles.RequiresCustomerAssignments(user.Role) && user.GetCustomerCodes().Count == 0)
        {
            return Errors.User.UpdateFailed($"At least one assigned customer code is required for {user.Role} role");
        }

        if (ApplicationRoles.RequiresAssignedSection(user.Role) && string.IsNullOrWhiteSpace(user.AssignedSection))
        {
            return Errors.User.UpdateFailed($"An assigned section is required for {user.Role} role");
        }

        user.UpdatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("User {Username} updated by admin", user.Username);

        try { await auditService.LogAsync(AuditActions.UpdateUser, "User", command.Id.ToString(), $"User {user.Username} updated", true); } catch { }

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
