using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Extensions;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.Notifications;
using ShopInventory.Models;
using ShopInventory.Services;
using BC = BCrypt.Net.BCrypt;

namespace ShopInventory.Features.Users.Commands.CreateUser;

public sealed class CreateUserHandler(
    ApplicationDbContext context,
    IAuditService auditService,
    INotificationService notificationService,
    ILogger<CreateUserHandler> logger
) : IRequestHandler<CreateUserCommand, ErrorOr<UserCreatedResponseDto>>
{
    public async Task<ErrorOr<UserCreatedResponseDto>> Handle(
        CreateUserCommand command,
        CancellationToken cancellationToken)
    {
        var request = command.Request;

        if (string.IsNullOrEmpty(request.Username) || string.IsNullOrEmpty(request.Password))
        {
            return Errors.User.CreationFailed("Username and password are required");
        }

        if (await context.Users.WhereUsernameOrEmailMatches(request.Username).AnyAsync(cancellationToken))
        {
            return Errors.User.DuplicateUsername;
        }

        if (!string.IsNullOrEmpty(request.Email) && await context.Users.WhereUsernameOrEmailMatches(request.Email).AnyAsync(cancellationToken))
        {
            return Errors.User.DuplicateEmail;
        }

        if (!ApplicationRoles.IsAssignableRole(request.Role))
        {
            return Errors.User.CreationFailed($"Invalid role. Valid roles are: {ApplicationRoles.DescribeAssignableRoles()}");
        }

        if (ApplicationRoles.RequiresWarehouseAssignments(request.Role) &&
            (request.AssignedWarehouseCodes == null || request.AssignedWarehouseCodes.Count == 0))
        {
            return Errors.User.CreationFailed($"At least one assigned warehouse code is required for {request.Role} role");
        }

        if (ApplicationRoles.RequiresCustomerAssignments(request.Role) &&
            (request.AssignedCustomerCodes == null || request.AssignedCustomerCodes.Count == 0))
        {
            return Errors.User.CreationFailed($"At least one assigned customer code is required for {request.Role} role");
        }

        if (ApplicationRoles.RequiresAssignedSection(request.Role) &&
            string.IsNullOrWhiteSpace(request.AssignedSection))
        {
            return Errors.User.CreationFailed($"An assigned section is required for {request.Role} role");
        }

        // A van account is only complete with a business partner, a cost centre and a supplying
        // warehouse on it, and CreateUserRequest carries none of the three. Accepting the role
        // here would open a van rep with no route-customer scope and no depot to draw stock from,
        // which fails later and silently, so refuse and name the path that does take the full form.
        if (ApplicationRoles.UsesLegacyRouteCustomerScope(request.Role))
        {
            return Errors.User.CreationFailed(
                $"{request.Role} accounts need van sales assignments this endpoint cannot set. Create them through /api/usermanagement.");
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = request.Username,
            PasswordHash = BC.HashPassword(request.Password),
            Email = request.Email,
            Role = request.Role.Trim(),
            FirstName = request.FirstName,
            LastName = request.LastName,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        if (ApplicationRoles.SupportsWarehouseAssignments(request.Role))
            user.SetWarehouseCodes(request.AssignedWarehouseCodes);

        if (ApplicationRoles.SupportsCustomerAssignments(request.Role))
            user.SetCustomerCodes(request.AssignedCustomerCodes);

        if (ApplicationRoles.RequiresAssignedSection(request.Role))
            user.AssignedSection = request.AssignedSection;

        context.Users.Add(user);
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("User {Username} created by admin", user.Username);

        try { await auditService.LogAsync(AuditActions.CreateUser, "User", user.Id.ToString(), $"User {user.Username} created with role {user.Role}", true); } catch { }

        // Account changes went to the audit trail and nowhere else, so nobody learned that an
        // account had been opened unless they went looking. Security + /user-management resolves to
        // Admin only, which is who this is for.
        try
        {
            await notificationService.CreateNotificationAsync(
                ModuleNotificationFactory.CreateBroadcastNotification(
                    $"User Account Created: {user.Username}",
                    $"Account {user.Username} was created with the {user.Role} role.",
                    "Info",
                    "Security",
                    "User",
                    user.Id.ToString(),
                    "/user-management",
                    new Dictionary<string, string>
                    {
                        ["userId"] = user.Id.ToString(),
                        ["username"] = user.Username,
                        ["role"] = user.Role,
                        ["isActive"] = user.IsActive.ToString().ToLowerInvariant()
                    }),
                cancellationToken);
        }
        catch (Exception notificationException)
        {
            logger.LogWarning(
                notificationException,
                "Failed to publish user creation notification for {Username}",
                user.Username);
        }

        return new UserCreatedResponseDto
        {
            Message = "User created successfully",
            User = new UserDto
            {
                Id = user.Id,
                Username = user.Username,
                Email = user.Email,
                Role = user.Role,
                FirstName = user.FirstName,
                LastName = user.LastName,
                IsActive = user.IsActive,
                CreatedAt = user.CreatedAt,
                AssignedWarehouseCodes = user.GetWarehouseCodes(),
                AssignedCustomerCodes = user.GetCustomerCodes(),
                AssignedSection = user.AssignedSection
            }
        };
    }
}
