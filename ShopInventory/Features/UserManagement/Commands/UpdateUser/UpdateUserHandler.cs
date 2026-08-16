using System.Security.Claims;
using System.Text.Json;
using ErrorOr;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Extensions;
using ShopInventory.Common.Security;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Features.UserManagement;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.UserManagement.Commands.UpdateUser;

public sealed class UpdateUserHandler(
    ApplicationDbContext context,
    IHttpContextAccessor httpContextAccessor,
    IMemoryCache memoryCache,
    IAuditService auditService,
    IBusinessPartnerService businessPartnerService,
    INotificationService notificationService,
    ILogger<UpdateUserHandler> logger
) : IRequestHandler<UpdateUserCommand, ErrorOr<Success>>
{
    private const string EffectivePermissionsCacheKeyPrefix = "user-permissions:";

    public async Task<ErrorOr<Success>> Handle(
        UpdateUserCommand command,
        CancellationToken cancellationToken)
    {
        var currentUserId = UserClaimReader.GetUserId(httpContextAccessor.HttpContext?.User);
        if (currentUserId is null)
        {
            return Errors.UserManagement.Unauthenticated;
        }

        var currentUser = await context.Users
            .AsNoTracking()
            .Where(current => current.Id == currentUserId.Value)
            .Select(current => new { current.Role, current.IsActive })
            .FirstOrDefaultAsync(cancellationToken);

        if (currentUser is null || !currentUser.IsActive)
        {
            return Errors.UserManagement.Unauthenticated;
        }

        var user = await context.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == command.Id, cancellationToken);

        if (user == null)
        {
            return Errors.UserManagement.NotFound(command.Id);
        }

        var currentRole = user.Role;
        var currentAssignedCustomerCodes = user.GetCustomerCodes()
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (string.Equals(currentUser.Role, ApplicationRoles.PodOperator, StringComparison.OrdinalIgnoreCase))
        {
            var requestedRole = string.IsNullOrWhiteSpace(command.Request.Role)
                ? user.Role
                : command.Request.Role;

            if (!string.Equals(currentRole, ApplicationRoles.Driver, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(requestedRole, ApplicationRoles.Driver, StringComparison.OrdinalIgnoreCase))
            {
                return Errors.UserManagement.PodOperatorCanOnlyManageDrivers;
            }

            if (command.Request.Permissions is { Count: > 0 })
            {
                return Errors.UserManagement.PodOperatorCannotAssignCustomPermissionsOnUpdate;
            }
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
                    return Errors.UserManagement.DuplicateUsername;
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
                return Errors.UserManagement.DuplicateEmail;
            }

            user.Email = command.Request.Email;
            user.EmailVerified = false;
        }

        if (command.Request.FirstName != null) user.FirstName = command.Request.FirstName;
        if (command.Request.LastName != null) user.LastName = command.Request.LastName;
        if (command.Request.IsActive.HasValue) user.IsActive = command.Request.IsActive.Value;

        if (!string.IsNullOrWhiteSpace(command.Request.Role))
        {
            if (!ApplicationRoles.CanAssignOrRetainManagedRole(command.Request.Role, user.Role))
            {
                return Errors.UserManagement.UpdateFailed($"Invalid role. Valid roles: {ApplicationRoles.DescribeAssignableRoles()}");
            }

            user.Role = command.Request.Role.Trim();
        }

        if (command.Request.AssignedWarehouseCodes != null)
        {
            if (ApplicationRoles.SupportsWarehouseAssignments(user.Role))
                user.SetWarehouseCodes(command.Request.AssignedWarehouseCodes);
            else
                user.SetWarehouseCodes(null);
        }

        if (command.Request.AssignedCustomerCodes != null)
        {
            if (ApplicationRoles.SupportsCustomerAssignments(user.Role))
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

        if (ApplicationRoles.RequiresAssignedSection(user.Role))
            user.AssignedSection = command.Request.AssignedSection;
        else
            user.AssignedSection = null;

        if (ApplicationRoles.RequiresAssignedBusinessPartnerCode(user.Role))
        {
            user.AssignedBusinessPartnerCode = command.Request.AssignedBusinessPartnerCode?.Trim();
            user.AssignedCostCentreCode = command.Request.AssignedCostCentreCode?.Trim();
        }
        else
        {
            user.AssignedBusinessPartnerCode = null;
            user.AssignedCostCentreCode = null;
        }

        if (ApplicationRoles.RequiresSupplyingWarehouseCode(user.Role))
        {
            user.SupplyingWarehouseCode = command.Request.SupplyingWarehouseCode?.Trim();

            // Optional, so only a positive id is taken — an unset picker posts 0, which would be a
            // foreign key to nothing.
            user.RouteId = command.Request.RouteId is > 0 ? command.Request.RouteId : null;
        }
        else
        {
            user.SupplyingWarehouseCode = null;

            // A route belongs to a van. A user moved off a van role keeps no claim on one.
            user.RouteId = null;
        }

        if (ApplicationRoles.SupportsFiscalDevice(user.Role))
        {
            // Optional, so an empty field posts null and a cleared one posts 0 — neither is a device.
            var requestedDevice = command.Request.FiscalDeviceId is > 0
                ? command.Request.FiscalDeviceId
                : null;

            if (requestedDevice != user.FiscalDeviceId)
            {
                var conflict = await FindFiscalDeviceHolderAsync(requestedDevice, user.Id, cancellationToken);
                if (conflict is not null)
                {
                    return Errors.UserManagement.UpdateFailed(
                        $"Fiscal device {requestedDevice} is already registered to {conflict}. A device's " +
                        "receipt chain has one writer: two handsets signing as it would each sign a different " +
                        "receipt as the same number, and ZIMRA refuses the whole fiscal day. Clear it there first.");
                }
            }

            user.FiscalDeviceId = requestedDevice;
        }
        else
        {
            // A device is registered to a handset that sells. A user moved off a van role stops being one.
            user.FiscalDeviceId = null;
        }

        if (ApplicationRoles.RequiresWarehouseAssignments(user.Role) && user.GetWarehouseCodes().Count == 0)
        {
            return Errors.UserManagement.UpdateFailed($"At least one assigned warehouse code is required for {user.Role} role");
        }

        if (ApplicationRoles.RequiresCustomerAssignments(user.Role) && user.GetCustomerCodes().Count == 0)
        {
            return Errors.UserManagement.UpdateFailed($"At least one assigned customer code is required for {user.Role} role");
        }

        if (ApplicationRoles.RequiresAssignedSection(user.Role) && string.IsNullOrWhiteSpace(user.AssignedSection))
        {
            return Errors.UserManagement.UpdateFailed($"An assigned section is required for {user.Role} role");
        }

        if (ApplicationRoles.RequiresAssignedBusinessPartnerCode(user.Role) &&
            string.IsNullOrWhiteSpace(user.AssignedBusinessPartnerCode))
        {
            return Errors.UserManagement.UpdateFailed($"An assigned business partner code is required for {user.Role} role");
        }

        if (ApplicationRoles.RequiresAssignedCostCentreCode(user.Role) &&
            string.IsNullOrWhiteSpace(user.AssignedCostCentreCode))
        {
            return Errors.UserManagement.UpdateFailed($"An assigned cost centre code is required for {user.Role} role");
        }

        if (ApplicationRoles.RequiresSupplyingWarehouseCode(user.Role) &&
            string.IsNullOrWhiteSpace(user.SupplyingWarehouseCode))
        {
            return Errors.UserManagement.UpdateFailed($"A supplying warehouse code is required for {user.Role} role");
        }

        var shouldNotifyCustomerAssignmentChanges = command.Request.AssignedCustomerCodes != null &&
            (string.Equals(user.Role, ApplicationRoles.Merchandiser, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(user.Role, ApplicationRoles.Driver, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(user.Role, ApplicationRoles.PodOperator, StringComparison.OrdinalIgnoreCase));

        var updatedAssignedCustomerCodes = user.GetCustomerCodes()
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var addedCustomerCodes = shouldNotifyCustomerAssignmentChanges
            ? updatedAssignedCustomerCodes
                .Except(currentAssignedCustomerCodes, StringComparer.OrdinalIgnoreCase)
                .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];

        var removedCustomerCodes = shouldNotifyCustomerAssignmentChanges
            ? currentAssignedCustomerCodes
                .Except(updatedAssignedCustomerCodes, StringComparer.OrdinalIgnoreCase)
                .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];

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
                .SetProperty(x => x.AssignedBusinessPartnerCode, user.AssignedBusinessPartnerCode)
                .SetProperty(x => x.AssignedCostCentreCode, user.AssignedCostCentreCode)
                .SetProperty(x => x.SupplyingWarehouseCode, user.SupplyingWarehouseCode)
                .SetProperty(x => x.RouteId, user.RouteId)
                .SetProperty(x => x.FiscalDeviceId, user.FiscalDeviceId)
                .SetProperty(x => x.Permissions, user.Permissions)
                .SetProperty(x => x.UpdatedAt, user.UpdatedAt),
                cancellationToken);

        if (rowsAffected == 0)
        {
            logger.LogError("UpdateUser wrote 0 rows for user {UserId}", command.Id);
            return Errors.UserManagement.UpdateFailed("Update failed: no rows were modified. Please try again.");
        }

        memoryCache.Remove(GetEffectivePermissionsCacheKey(command.Id));
        logger.LogInformation("User {UserId} updated to username {Username}", command.Id, user.Username);

        if (shouldNotifyCustomerAssignmentChanges)
        {
            var customerNamesByCode = addedCustomerCodes.Count > 0 || removedCustomerCodes.Count > 0
                ? await CustomerAssignmentNotificationCustomerResolver.ResolveNamesAsync(
                    businessPartnerService,
                    addedCustomerCodes.Concat(removedCustomerCodes),
                    cancellationToken)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (addedCustomerCodes.Count > 0)
            {
                var addedNotification = CustomerAssignmentNotificationFactory.CreateForUser(
                    user.Username,
                    user.Role,
                    addedCustomerCodes,
                    customerNamesByCode,
                    isRemoval: false,
                    user.Id);

                if (addedNotification is not null)
                {
                    await notificationService.CreateNotificationAsync(addedNotification, cancellationToken);
                }
            }

            if (removedCustomerCodes.Count > 0)
            {
                var removedNotification = CustomerAssignmentNotificationFactory.CreateForUser(
                    user.Username,
                    user.Role,
                    removedCustomerCodes,
                    customerNamesByCode,
                    isRemoval: true,
                    user.Id);

                if (removedNotification is not null)
                {
                    await notificationService.CreateNotificationAsync(removedNotification, cancellationToken);
                }
            }
        }

        try
        {
            var assignmentAuditDetail = shouldNotifyCustomerAssignmentChanges &&
                                        (addedCustomerCodes.Count > 0 || removedCustomerCodes.Count > 0)
                ? $"; customer assignments added={addedCustomerCodes.Count}, removed={removedCustomerCodes.Count}"
                : string.Empty;

            await auditService.LogAsync(
                AuditActions.UpdateUser,
                "User",
                command.Id.ToString(),
                $"User {user.Username} updated{assignmentAuditDetail}",
                true);
        }
        catch
        {
        }

        return Result.Success;
    }

    /// <summary>
    /// The account already registered as <paramref name="deviceId"/>, if any, named the way an admin
    /// can act on.
    /// </summary>
    /// <remarks>
    /// Deactivated accounts count. A device id sitting on a dormant account is still the id ZIMRA has
    /// registered, and reactivating that account behind a second holder is exactly the fork this guard
    /// exists to prevent — so it is refused here and cleared by hand, deliberately.
    /// </remarks>
    private async Task<string?> FindFiscalDeviceHolderAsync(
        int? deviceId,
        Guid excludingUserId,
        CancellationToken cancellationToken)
    {
        if (deviceId is null)
        {
            return null;
        }

        return await context.Users
            .AsNoTracking()
            .Where(other => other.Id != excludingUserId && other.FiscalDeviceId == deviceId)
            .OrderBy(other => other.Username)
            .Select(other => other.Username)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static string GetEffectivePermissionsCacheKey(Guid userId)
    {
        return $"{EffectivePermissionsCacheKeyPrefix}{userId}";
    }
}
