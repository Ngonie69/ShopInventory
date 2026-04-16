using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
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
    private static readonly string[] ValidRoles = { "Admin", "Cashier", "StockController", "DepotController", "PodOperator", "Driver", "Merchandiser", "SalesRep" };

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

        if (!string.IsNullOrEmpty(request.Email) && request.Email != user.Email)
        {
            if (await context.Users.AnyAsync(u => u.Email == request.Email && u.Id != command.Id, cancellationToken))
            {
                return Errors.User.UpdateFailed("Email already exists");
            }
            user.Email = request.Email;
        }

        if (!string.IsNullOrEmpty(request.Role))
        {
            if (!ValidRoles.Contains(request.Role))
            {
                return Errors.User.UpdateFailed($"Invalid role. Valid roles are: {string.Join(", ", ValidRoles)}");
            }
            user.Role = request.Role;
        }

        if (request.FirstName != null) user.FirstName = request.FirstName;
        if (request.LastName != null) user.LastName = request.LastName;
        if (request.IsActive.HasValue) user.IsActive = request.IsActive.Value;

        if (request.AssignedWarehouseCodes != null)
        {
            if (user.Role == "StockController" || user.Role == "DepotController")
                user.SetWarehouseCodes(request.AssignedWarehouseCodes);
            else
                user.SetWarehouseCodes(null);
        }

        if (request.AssignedCustomerCodes != null)
        {
            if (user.Role == "Merchandiser")
                user.SetCustomerCodes(request.AssignedCustomerCodes);
            else
                user.SetCustomerCodes(null);
        }

        if (user.Role == "Driver")
            user.AssignedSection = request.AssignedSection;
        else
            user.AssignedSection = null;

        if ((user.Role == "StockController" || user.Role == "DepotController") && user.GetWarehouseCodes().Count == 0)
        {
            return Errors.User.UpdateFailed($"At least one assigned warehouse code is required for {user.Role} role");
        }

        if (user.Role == "Merchandiser" && user.GetCustomerCodes().Count == 0)
        {
            return Errors.User.UpdateFailed("At least one assigned customer code is required for Merchandiser role");
        }

        if (user.Role == "Driver" && string.IsNullOrWhiteSpace(user.AssignedSection))
        {
            return Errors.User.UpdateFailed("An assigned section is required for Driver role");
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
