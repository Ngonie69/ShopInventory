using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.Features.UserManagement;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.UserManagement.Commands.UpdateMerchandiserAssignedCustomers;

public sealed class UpdateMerchandiserAssignedCustomersHandler(
    ApplicationDbContext context,
    IAuditService auditService,
    IBusinessPartnerService businessPartnerService,
    INotificationService notificationService,
    ILogger<UpdateMerchandiserAssignedCustomersHandler> logger
) : IRequestHandler<UpdateMerchandiserAssignedCustomersCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        UpdateMerchandiserAssignedCustomersCommand command,
        CancellationToken cancellationToken)
    {
        var merchandiser = await context.Users
            .AsTracking()
            .FirstOrDefaultAsync(user => user.Id == command.Id, cancellationToken);

        if (merchandiser is null)
        {
            return Errors.UserManagement.NotFound(command.Id);
        }

        if (!string.Equals(merchandiser.Role, "Merchandiser", StringComparison.OrdinalIgnoreCase))
        {
            return Errors.UserManagement.UpdateFailed("Only merchandiser accounts can be updated through this workflow.");
        }

        var currentAssignedCustomerCodes = merchandiser.GetCustomerCodes()
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code)
            .ToList();

        var assignedCustomerCodes = command.Request.AssignedCustomerCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code)
            .ToList();

        var addedCustomerCodes = assignedCustomerCodes
            .Except(currentAssignedCustomerCodes, StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code)
            .ToList();

        var removedCustomerCodes = currentAssignedCustomerCodes
            .Except(assignedCustomerCodes, StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code)
            .ToList();

        var assignedWarehouseCodes = command.Request.AssignedWarehouseCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code)
            .ToList();

        merchandiser.SetWarehouseCodes(assignedWarehouseCodes);
        merchandiser.SetCustomerCodes(assignedCustomerCodes);
        merchandiser.UpdatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync(cancellationToken);

        var customerNamesByCode = addedCustomerCodes.Count > 0 || removedCustomerCodes.Count > 0
            ? await CustomerAssignmentNotificationCustomerResolver.ResolveNamesAsync(
                businessPartnerService,
                addedCustomerCodes.Concat(removedCustomerCodes),
                cancellationToken)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (addedCustomerCodes.Count > 0)
        {
            var addedNotification = CustomerAssignmentNotificationFactory.CreateForUser(
                merchandiser.Username,
                merchandiser.Role,
                addedCustomerCodes,
                customerNamesByCode,
                isRemoval: false,
                merchandiser.Id);

            if (addedNotification is not null)
            {
                await notificationService.CreateNotificationAsync(addedNotification, cancellationToken);
            }
        }

        if (removedCustomerCodes.Count > 0)
        {
            var removedNotification = CustomerAssignmentNotificationFactory.CreateForUser(
                merchandiser.Username,
                merchandiser.Role,
                removedCustomerCodes,
                customerNamesByCode,
                isRemoval: true,
                merchandiser.Id);

            if (removedNotification is not null)
            {
                await notificationService.CreateNotificationAsync(removedNotification, cancellationToken);
            }
        }

        var revokedRefreshTokenCount = await context.RefreshTokens
            .Where(token => token.UserId == merchandiser.Id && !token.IsRevoked && token.ExpiresAt > DateTime.UtcNow)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(token => token.IsRevoked, true)
                .SetProperty(token => token.RevokedAt, _ => DateTime.UtcNow)
                .SetProperty(token => token.RevokedByIp, _ => "system")
                .SetProperty(token => token.ReasonRevoked, _ => "Merchandiser assignments updated"),
                cancellationToken);

        logger.LogInformation(
            "Updated merchandiser assignments for {Username} ({UserId}); revoked {RefreshTokenCount} active refresh tokens",
            merchandiser.Username,
            merchandiser.Id,
            revokedRefreshTokenCount);

        try
        {
            await auditService.LogAsync(
                AuditActions.UpdateUser,
                "User",
                merchandiser.Id.ToString(),
                $"Updated merchandiser assignments for {merchandiser.Username}",
                true);
        }
        catch
        {
        }

        return Result.Success;
    }
}