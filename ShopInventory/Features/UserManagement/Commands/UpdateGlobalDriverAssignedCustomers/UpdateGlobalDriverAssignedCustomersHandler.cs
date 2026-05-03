using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.UserManagement.Commands.UpdateGlobalDriverAssignedCustomers;

public sealed class UpdateGlobalDriverAssignedCustomersHandler(
    ApplicationDbContext context,
    IAuditService auditService,
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
            .OrderBy(code => code)
            .ToList();

        var driverIds = await context.Users
            .AsNoTracking()
            .Where(user => user.Role == "Driver")
            .Select(user => user.Id)
            .ToListAsync(cancellationToken);

        if (driverIds.Count == 0)
        {
            logger.LogInformation("No driver accounts found while updating global driver business partners");
            return 0;
        }

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

        try
        {
            await auditService.LogAsync(
                AuditActions.UpdateUser,
                "User",
                "DriverRole",
                $"Updated global driver business partner scope for {updatedDriverCount} drivers",
                true);
        }
        catch
        {
        }

        return updatedDriverCount;
    }
}