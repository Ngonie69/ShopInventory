using System.Text.Json;
using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Extensions;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.UserManagement.Commands.UpdateUser;

public sealed class UpdateUserHandler(
    ApplicationDbContext context,
    IMemoryCache memoryCache,
    IAuditService auditService,
    ILogger<UpdateUserHandler> logger
) : IRequestHandler<UpdateUserCommand, ErrorOr<Success>>
{
    private const string EffectivePermissionsCacheKeyPrefix = "user-permissions:";

    private static readonly string[] ValidRoles =
    [
        "Admin",
        "Manager",
        "User",
        "ReadOnly",
        "Cashier",
        "StockController",
        "DepotController",
        "PodOperator",
        "Driver",
        "Merchandiser",
        "SalesRep",
        "MerchandiserPurchaseOrderViewer",
        "Lab"
    ];

    public async Task<ErrorOr<Success>> Handle(
        UpdateUserCommand command,
        CancellationToken cancellationToken)
    {
        var user = await context.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == command.Id, cancellationToken);

        if (user == null)
        {
            return Errors.UserManagement.NotFound(command.Id);
        }

        if (command.Request.Username != null)
        {
            var requestedUsername = command.Request.Username.Trim();
            if (string.IsNullOrWhiteSpace(requestedUsername))
            {
                return Errors.UserManagement.UpdateFailed("Username is required");
            }

            if (!string.Equals(requestedUsername, user.Username, StringComparison.Ordinal))
            {
                var usernameExists = await context.Users
                    .Where(u => u.Id != command.Id)
                    .WhereUsernameMatches(requestedUsername)
                    .AnyAsync(cancellationToken);

                if (usernameExists)
                {
                    return Errors.UserManagement.UpdateFailed("Username already exists");
                }

                user.Username = requestedUsername;
            }
        }

        if (!string.IsNullOrWhiteSpace(command.Request.Email) &&
            !string.Equals(command.Request.Email, user.Email, StringComparison.OrdinalIgnoreCase))
        {
            var emailExists = await context.Users
                .Where(u => u.Id != command.Id)
                .WhereEmailMatches(command.Request.Email)
                .AnyAsync(cancellationToken);

            if (emailExists)
            {
                return Errors.UserManagement.UpdateFailed("Email already exists");
            }

            user.Email = command.Request.Email;
            user.EmailVerified = false;
        }

        if (command.Request.FirstName != null) user.FirstName = command.Request.FirstName;
        if (command.Request.LastName != null) user.LastName = command.Request.LastName;
        if (command.Request.IsActive.HasValue) user.IsActive = command.Request.IsActive.Value;

        if (!string.IsNullOrWhiteSpace(command.Request.Role))
        {
            if (!ValidRoles.Contains(command.Request.Role))
            {
                return Errors.UserManagement.UpdateFailed($"Invalid role. Valid roles: {string.Join(", ", ValidRoles)}");
            }

            user.Role = command.Request.Role;
        }

        if (command.Request.AssignedWarehouseCodes != null)
        {
            if (user.Role == "StockController" || user.Role == "DepotController" || user.Role == "Merchandiser")
                user.SetWarehouseCodes(command.Request.AssignedWarehouseCodes);
            else
                user.SetWarehouseCodes(null);
        }

        if (command.Request.AssignedCustomerCodes != null)
        {
            if (user.Role == "Merchandiser" || user.Role == "Driver")
            {
                logger.LogInformation("Setting customer codes for {User}: {Codes}", user.Username, string.Join(",", command.Request.AssignedCustomerCodes));
                user.SetCustomerCodes(command.Request.AssignedCustomerCodes);
                logger.LogInformation("After SetCustomerCodes, raw value: {Raw}", user.AssignedCustomerCodes ?? "NULL");
            }
            else
            {
                user.SetCustomerCodes(null);
            }
        }

        if (user.Role == "Driver")
            user.AssignedSection = command.Request.AssignedSection;
        else
            user.AssignedSection = null;

        if (command.Request.AllowedPaymentMethods != null)
        {
            user.SetAllowedPaymentMethods(command.Request.AllowedPaymentMethods);
        }

        if (command.Request.DefaultGLAccount != null)
        {
            user.DefaultGLAccount = string.IsNullOrWhiteSpace(command.Request.DefaultGLAccount)
                ? null
                : command.Request.DefaultGLAccount;
        }

        if (command.Request.AllowedPaymentBusinessPartners != null)
        {
            user.SetAllowedPaymentBusinessPartners(command.Request.AllowedPaymentBusinessPartners);
        }

        if ((user.Role == "StockController" || user.Role == "DepotController") && user.GetWarehouseCodes().Count == 0)
        {
            return Errors.UserManagement.UpdateFailed($"At least one assigned warehouse code is required for {user.Role} role");
        }

        if (user.Role == "Merchandiser" && user.GetCustomerCodes().Count == 0)
        {
            return Errors.UserManagement.UpdateFailed("At least one assigned customer code is required for Merchandiser role");
        }

        if (user.Role == "Driver" && string.IsNullOrWhiteSpace(user.AssignedSection))
        {
            return Errors.UserManagement.UpdateFailed("An assigned section is required for Driver role");
        }

        if (command.Request.Permissions != null)
        {
            if (command.Request.Permissions.Count == 0)
            {
                user.Permissions = JsonSerializer.Serialize(Permission.GetDefaultPermissionsForRole(user.Role));
            }
            else
            {
                var allPermissions = Permission.GetAllPermissions();
                var invalidPermissions = command.Request.Permissions.Except(allPermissions).ToList();
                if (invalidPermissions.Count > 0)
                {
                    return Errors.UserManagement.UpdateFailed($"Invalid permissions: {string.Join(", ", invalidPermissions)}");
                }

                user.Permissions = JsonSerializer.Serialize(command.Request.Permissions);
            }
        }

        user.UpdatedAt = DateTime.UtcNow;

        var rowsAffected = await context.Users
            .Where(u => u.Id == command.Id)
            .ExecuteUpdateAsync(u => u
                .SetProperty(x => x.Username, user.Username)
                .SetProperty(x => x.Email, user.Email)
                .SetProperty(x => x.EmailVerified, user.EmailVerified)
                .SetProperty(x => x.FirstName, user.FirstName)
                .SetProperty(x => x.LastName, user.LastName)
                .SetProperty(x => x.IsActive, user.IsActive)
                .SetProperty(x => x.Role, user.Role)
                .SetProperty(x => x.AssignedWarehouseCodes, user.AssignedWarehouseCodes)
                .SetProperty(x => x.AssignedCustomerCodes, user.AssignedCustomerCodes)
                .SetProperty(x => x.AssignedSection, user.AssignedSection)
                .SetProperty(x => x.Permissions, user.Permissions)
                .SetProperty(x => x.AllowedPaymentMethods, user.AllowedPaymentMethods)
                .SetProperty(x => x.DefaultGLAccount, user.DefaultGLAccount)
                .SetProperty(x => x.AllowedPaymentBusinessPartners, user.AllowedPaymentBusinessPartners)
                .SetProperty(x => x.UpdatedAt, user.UpdatedAt),
                cancellationToken);

        if (rowsAffected == 0)
        {
            logger.LogError("UpdateUser wrote 0 rows for user {UserId}", command.Id);
            return Errors.UserManagement.UpdateFailed("Update failed: no rows were modified. Please try again.");
        }

        memoryCache.Remove(GetEffectivePermissionsCacheKey(command.Id));
        logger.LogInformation("User {UserId} updated to username {Username}", command.Id, user.Username);

        try
        {
            await auditService.LogAsync(AuditActions.UpdateUser, "User", command.Id.ToString(), $"User {user.Username} updated", true);
        }
        catch
        {
        }

        return Result.Success;
    }

    private static string GetEffectivePermissionsCacheKey(Guid userId)
    {
        return $"{EffectivePermissionsCacheKeyPrefix}{userId}";
    }
}
