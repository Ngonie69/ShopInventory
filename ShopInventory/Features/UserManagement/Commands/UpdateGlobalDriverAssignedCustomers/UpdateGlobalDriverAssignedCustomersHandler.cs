using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Features.UserManagement;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.UserManagement.Commands.UpdateGlobalDriverAssignedCustomers;

public sealed class UpdateGlobalDriverAssignedCustomersHandler(
    ApplicationDbContext context,
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
        var assignedCustomerCodes = command.Request.AssignedCustomerCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var drivers = await context.Users
            .AsNoTracking()
            .Where(user => user.Role == "Driver")
            .OrderBy(user => user.Username)
            .ToListAsync(cancellationToken);

        var driverIds = drivers
            .Select(user => user.Id)
            .ToList();

        if (driverIds.Count == 0)
        {
            logger.LogInformation("No driver accounts found while updating global driver business partners");
            return 0;
        }

        var currentAssignedCustomerCodes = drivers[0].GetCustomerCodes()
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

        var updatedDriverCount = await context.Users
            .Where(user => user.Role == "Driver")
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(user => user.AssignedCustomerCodes, _ => serializedCodes)
                .SetProperty(user => user.UpdatedAt, _ => DateTime.UtcNow),
                cancellationToken);

        var revokedRefreshTokenCount = await context.RefreshTokens
            .Where(token => driverIds.Contains(token.UserId) && !token.IsRevoked && token.ExpiresAt > DateTime.UtcNow)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(token => token.IsRevoked, true)
                .SetProperty(token => token.RevokedAt, _ => DateTime.UtcNow)
                .SetProperty(token => token.RevokedByIp, _ => "system")
                .SetProperty(token => token.ReasonRevoked, _ => "Driver business partner scope updated"),
                cancellationToken);

        logger.LogInformation(
            "Updated global driver business partners for {UpdatedDriverCount} drivers; revoked {RefreshTokenCount} active refresh tokens",
            updatedDriverCount,
            revokedRefreshTokenCount);

        var customerNamesByCode = addedCustomerCodes.Count > 0 || removedCustomerCodes.Count > 0
            ? await CustomerAssignmentNotificationCustomerResolver.ResolveNamesAsync(
                businessPartnerService,
                addedCustomerCodes.Concat(removedCustomerCodes),
                cancellationToken)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (addedCustomerCodes.Count > 0)
        {
            var addedNotification = CustomerAssignmentNotificationFactory.CreateForRole(
                "Driver",
                "Driver",
                addedCustomerCodes,
                customerNamesByCode,
                isRemoval: false);

            if (addedNotification is not null)
            {
                await notificationService.CreateNotificationAsync(addedNotification, cancellationToken);
            }
        }

        if (removedCustomerCodes.Count > 0)
        {
            var removedNotification = CustomerAssignmentNotificationFactory.CreateForRole(
                "Driver",
                "Driver",
                removedCustomerCodes,
                customerNamesByCode,
                isRemoval: true);

            if (removedNotification is not null)
            {
                await notificationService.CreateNotificationAsync(removedNotification, cancellationToken);
            }
        }

        try
        {
            await auditService.LogAsync(
                AuditActions.UpdateUser,
                "User",
                "DriverRole",
                $"Updated global driver business partner scope for {updatedDriverCount} drivers; added={addedCustomerCodes.Count}, removed={removedCustomerCodes.Count}",
                true);
        }
        catch
        {
        }

        return updatedDriverCount;
    }
}