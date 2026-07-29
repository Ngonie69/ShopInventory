using ErrorOr;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.Features.UserManagement;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.UserManagement.Commands.UpdateGlobalDriverAssignedCustomers;

public sealed class UpdateGlobalDriverAssignedCustomersHandler(
    ApplicationDbContext context,
    IHttpContextAccessor httpContextAccessor,
    IAuditService auditService,
    IBusinessPartnerService businessPartnerService,
    INotificationService notificationService,
    ILogger<UpdateGlobalDriverAssignedCustomersHandler> logger
) : IRequestHandler<UpdateGlobalDriverAssignedCustomersCommand, ErrorOr<int>>
{
    public async Task<ErrorOr<int>> Handle(
        UpdateGlobalDriverAssignedCustomersCommand command,
        CancellationToken cancellationToken)
    {
        if (httpContextAccessor.HttpContext?.User.IsInRole(ApplicationRoles.PodOperator) == true)
        {
            return Errors.UserManagement.PodOperatorCanOnlyManageDrivers;
        }

        var assignedCustomerCodes = command.Request.AssignedCustomerCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var mobileUsers = await context.Users
            .AsNoTracking()
            .Where(user => user.Role == ApplicationRoles.Driver || user.Role == ApplicationRoles.PodOperator)
            .OrderBy(user => user.Username)
            .ToListAsync(cancellationToken);

        var mobileUserIds = mobileUsers
            .Select(user => user.Id)
            .ToList();

        if (mobileUserIds.Count == 0)
        {
            logger.LogInformation("No driver or pod operator accounts found while updating global mobile business partners");
            return 0;
        }

        var currentAssignedCustomerCodes = mobileUsers[0].GetCustomerCodes()
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var addedCustomerCodes = assignedCustomerCodes
            .Except(currentAssignedCustomerCodes, StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var removedCustomerCodes = currentAssignedCustomerCodes
            .Except(assignedCustomerCodes, StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var serializedCodes = assignedCustomerCodes.Count == 0
            ? null
            : System.Text.Json.JsonSerializer.Serialize(assignedCustomerCodes);

        var updatedUserCount = await context.Users
            .Where(user => user.Role == ApplicationRoles.Driver || user.Role == ApplicationRoles.PodOperator)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(user => user.AssignedCustomerCodes, _ => serializedCodes)
                .SetProperty(user => user.UpdatedAt, _ => DateTime.UtcNow),
                cancellationToken);

        var revokedRefreshTokenCount = await context.RefreshTokens
            .Where(token => mobileUserIds.Contains(token.UserId) && !token.IsRevoked && token.ExpiresAt > DateTime.UtcNow)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(token => token.IsRevoked, true)
                .SetProperty(token => token.RevokedAt, _ => DateTime.UtcNow)
                .SetProperty(token => token.RevokedByIp, _ => "system")
                .SetProperty(token => token.ReasonRevoked, _ => "Mobile business partner scope updated"),
                cancellationToken);

        logger.LogInformation(
            "Updated global mobile business partners for {UpdatedUserCount} driver/pod operator accounts; revoked {RefreshTokenCount} active refresh tokens",
            updatedUserCount,
            revokedRefreshTokenCount);

        var customerNamesByCode = addedCustomerCodes.Count > 0 || removedCustomerCodes.Count > 0
            ? await CustomerAssignmentNotificationCustomerResolver.ResolveNamesAsync(
                businessPartnerService,
                addedCustomerCodes.Concat(removedCustomerCodes),
                cancellationToken)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (addedCustomerCodes.Count > 0)
        {
            foreach (var role in ApplicationRoles.DriverScopedRoles)
            {
                var addedNotification = CustomerAssignmentNotificationFactory.CreateForRole(
                    role,
                    role,
                    addedCustomerCodes,
                    customerNamesByCode,
                    isRemoval: false);

                if (addedNotification is not null)
                {
                    await notificationService.CreateNotificationAsync(addedNotification, cancellationToken);
                }
            }
        }

        if (removedCustomerCodes.Count > 0)
        {
            foreach (var role in ApplicationRoles.DriverScopedRoles)
            {
                var removedNotification = CustomerAssignmentNotificationFactory.CreateForRole(
                    role,
                    role,
                    removedCustomerCodes,
                    customerNamesByCode,
                    isRemoval: true);

                if (removedNotification is not null)
                {
                    await notificationService.CreateNotificationAsync(removedNotification, cancellationToken);
                }
            }
        }

        try
        {
            await auditService.LogAsync(
                AuditActions.UpdateUser,
                "User",
                "MobileRole",
                $"Updated global mobile business partner scope for {updatedUserCount} driver/pod operator accounts; added={addedCustomerCodes.Count}, removed={removedCustomerCodes.Count}",
                true);
        }
        catch
        {
        }

        return updatedUserCount;
    }
}
