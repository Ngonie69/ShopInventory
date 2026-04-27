using ErrorOr;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Errors;
using ShopInventory.Data;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.UserManagement.Commands.UpdateMerchandiserAssignedCustomers;

public sealed class UpdateMerchandiserAssignedCustomersHandler(
    ApplicationDbContext context,
    IAuditService auditService,
    ILogger<UpdateMerchandiserAssignedCustomersHandler> logger
) : IRequestHandler<UpdateMerchandiserAssignedCustomersCommand, ErrorOr<Success>>
{
    public async Task<ErrorOr<Success>> Handle(
        UpdateMerchandiserAssignedCustomersCommand command,
        CancellationToken cancellationToken)
    {
        var merchandiser = await context.Users
            .FirstOrDefaultAsync(user => user.Id == command.Id, cancellationToken);

        if (merchandiser is null)
        {
            return Errors.UserManagement.NotFound(command.Id);
        }

        if (!string.Equals(merchandiser.Role, "Merchandiser", StringComparison.OrdinalIgnoreCase))
        {
            return Errors.UserManagement.UpdateFailed("Only merchandiser accounts can be updated through this workflow.");
        }

        var assignedCustomerCodes = command.Request.AssignedCustomerCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
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

        logger.LogInformation(
            "Updated merchandiser assignments for {Username} ({UserId})",
            merchandiser.Username,
            merchandiser.Id);

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