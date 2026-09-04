using ErrorOr;
using System.Security.Claims;
using System.Text.Json;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using ShopInventory.Common.Extensions;
using ShopInventory.Common.Errors;
using ShopInventory.Common.Security;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Services;

namespace ShopInventory.Features.UserManagement.Commands.CreateUser;

public sealed class CreateUserHandler(
    ApplicationDbContext context,
    IHttpContextAccessor httpContextAccessor,
    IAuditService auditService,
    ILogger<CreateUserHandler> logger
) : IRequestHandler<CreateUserCommand, ErrorOr<UserDetailDto>>
{
    public async Task<ErrorOr<UserDetailDto>> Handle(
        CreateUserCommand command,
        CancellationToken cancellationToken)
    {
        var request = command.Request;

        var currentUserId = UserClaimReader.GetUserId(httpContextAccessor.HttpContext?.User);
        if (currentUserId is null)
        {
            return Errors.UserManagement.Unauthenticated;
        }

        var currentUser = await context.Users
            .AsNoTracking()
            .Where(user => user.Id == currentUserId.Value)
            .Select(user => new { user.Role, user.IsActive })
            .FirstOrDefaultAsync(cancellationToken);

        if (currentUser is null || !currentUser.IsActive)
        {
            return Errors.UserManagement.Unauthenticated;
        }

        if (string.Equals(currentUser.Role, ApplicationRoles.SalesRep, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(request.Role, ApplicationRoles.Merchandiser, StringComparison.OrdinalIgnoreCase))
            {
                return Errors.UserManagement.SalesRepCanOnlyCreateMerchandisers;
            }

            if (request.Permissions is { Count: > 0 })
            {
                return Errors.UserManagement.SalesRepCannotAssignCustomPermissions;
            }
        }

        if (string.Equals(currentUser.Role, ApplicationRoles.PodOperator, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(request.Role, ApplicationRoles.Driver, StringComparison.OrdinalIgnoreCase))
            {
                return Errors.UserManagement.PodOperatorCanOnlyCreateDrivers;
            }

            if (request.Permissions is { Count: > 0 })
            {
                return Errors.UserManagement.PodOperatorCannotAssignCustomPermissions;
            }
        }

        if (await context.Users.WhereUsernameOrEmailMatches(request.Username).AnyAsync(cancellationToken))
        {
            return Errors.UserManagement.DuplicateUsername;
        }

        if (!string.IsNullOrWhiteSpace(request.Email) && await context.Users.WhereUsernameOrEmailMatches(request.Email).AnyAsync(cancellationToken))
        {
            return Errors.UserManagement.DuplicateEmail;
        }

        if (!ApplicationRoles.IsAssignableRole(request.Role))
        {
            return Errors.UserManagement.CreationFailed($"Invalid role. Valid roles: {ApplicationRoles.DescribeAssignableRoles()}");
        }

        if (ApplicationRoles.RequiresWarehouseAssignments(request.Role) &&
            (request.AssignedWarehouseCodes == null || request.AssignedWarehouseCodes.Count == 0))
        {
            return Errors.UserManagement.CreationFailed($"At least one assigned warehouse code is required for {request.Role} role");
        }

        if (ApplicationRoles.RequiresCustomerAssignments(request.Role) &&
            (request.AssignedCustomerCodes == null || request.AssignedCustomerCodes.Count == 0))
        {
            return Errors.UserManagement.CreationFailed($"At least one assigned customer code is required for {request.Role} role");
        }

        if (ApplicationRoles.RequiresAssignedSection(request.Role) &&
            string.IsNullOrWhiteSpace(request.AssignedSection))
        {
            return Errors.UserManagement.CreationFailed($"An assigned section is required for {request.Role} role");
        }

        if (ApplicationRoles.RequiresAssignedBusinessPartnerCode(request.Role) &&
            string.IsNullOrWhiteSpace(request.AssignedBusinessPartnerCode))
        {
            return Errors.UserManagement.CreationFailed($"An assigned business partner code is required for {request.Role} role");
        }

        if (ApplicationRoles.RequiresAssignedCostCentreCode(request.Role) &&
            string.IsNullOrWhiteSpace(request.AssignedCostCentreCode))
        {
            return Errors.UserManagement.CreationFailed($"An assigned cost centre code is required for {request.Role} role");
        }

        if (ApplicationRoles.RequiresSupplyingWarehouseCode(request.Role) &&
            string.IsNullOrWhiteSpace(request.SupplyingWarehouseCode))
        {
            return Errors.UserManagement.CreationFailed($"A supplying warehouse code is required for {request.Role} role");
        }

        // A till operator's selling identity comes from its shop, so the shop is required — and the
        // three loose codes are refused, because an account carrying both has two sources for one
        // answer and SellingAccountResolver reads only the shop. Rejecting the combination is what
        // stops an administrator believing the codes they typed are the ones the till will sell on.
        if (ApplicationRoles.RequiresShopAssignment(request.Role))
        {
            if (request.ShopId is null or <= 0)
            {
                return Errors.UserManagement.CreationFailed($"An assigned shop is required for {request.Role} role");
            }

            if (!string.IsNullOrWhiteSpace(request.AssignedBusinessPartnerCode) ||
                !string.IsNullOrWhiteSpace(request.AssignedCostCentreCode) ||
                !string.IsNullOrWhiteSpace(request.SupplyingWarehouseCode) ||
                request.AssignedWarehouseCodes is { Count: > 0 })
            {
                return Errors.UserManagement.CreationFailed(
                    $"A {request.Role} takes its business partner, warehouse and cost centre from its shop. Assign the shop only.");
            }

            var shop = await context.Shops
                .AsNoTracking()
                .FirstOrDefaultAsync(candidate => candidate.Id == request.ShopId, cancellationToken);

            if (shop is null)
            {
                return Errors.UserManagement.CreationFailed($"Shop {request.ShopId} was not found");
            }

            if (!shop.IsActive)
            {
                return Errors.UserManagement.CreationFailed($"{shop.Name} is closed, so a till operator cannot be assigned to it");
            }
        }
        else if (request.ShopId is not null)
        {
            return Errors.UserManagement.CreationFailed(
                $"A shop can only be assigned to a {ApplicationRoles.TillOperator}, not to {request.Role}");
        }

        // Optional, so an empty field posts null and a cleared one posts 0 — neither is a device.
        var fiscalDeviceId = ApplicationRoles.SupportsFiscalDevice(request.Role) && request.FiscalDeviceId is > 0
            ? request.FiscalDeviceId
            : null;

        if (fiscalDeviceId is not null)
        {
            // Deactivated accounts count: the id ZIMRA has registered does not lapse with the account,
            // and reactivating one behind a second holder forks the chain this guard exists to protect.
            var deviceHolder = await context.Users
                .AsNoTracking()
                .Where(user => user.FiscalDeviceId == fiscalDeviceId)
                .OrderBy(user => user.Username)
                .Select(user => user.Username)
                .FirstOrDefaultAsync(cancellationToken);

            if (deviceHolder is not null)
            {
                return Errors.UserManagement.CreationFailed(
                    $"Fiscal device {fiscalDeviceId} is already registered to {deviceHolder}. A device's " +
                    "receipt chain has one writer: two handsets signing as it would each sign a different " +
                    "receipt as the same number, and ZIMRA refuses the whole fiscal day. Clear it there first.");
            }
        }

        List<string> permissions;
        if (request.Permissions is { Count: > 0 })
        {
            var allPermissions = Permission.GetAllPermissions();
            var invalidPermissions = request.Permissions.Except(allPermissions).ToList();
            if (invalidPermissions.Count > 0)
            {
                return Errors.UserManagement.CreationFailed($"Invalid permissions: {string.Join(", ", invalidPermissions)}");
            }

            permissions = request.Permissions;
        }
        else
        {
            permissions = Permission.GetDefaultPermissionsForRole(request.Role);
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = request.Username,
            Email = request.Email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            FirstName = request.FirstName,
            LastName = request.LastName,
            Role = request.Role.Trim(),
            IsActive = true,
            EmailVerified = false,
            TwoFactorEnabled = false,
            Permissions = JsonSerializer.Serialize(permissions),
            CreatedAt = DateTime.UtcNow
        };

        if (ApplicationRoles.SupportsWarehouseAssignments(request.Role))
        {
            user.SetWarehouseCodes(request.AssignedWarehouseCodes);
        }

        if (ApplicationRoles.SupportsCustomerAssignments(request.Role))
        {
            user.SetCustomerCodes(request.AssignedCustomerCodes);
        }

        if (ApplicationRoles.RequiresAssignedSection(request.Role))
        {
            user.AssignedSection = request.AssignedSection;
        }

        if (ApplicationRoles.RequiresAssignedBusinessPartnerCode(request.Role))
        {
            user.AssignedBusinessPartnerCode = request.AssignedBusinessPartnerCode?.Trim();
            user.AssignedCostCentreCode = request.AssignedCostCentreCode?.Trim();
        }

        if (ApplicationRoles.RequiresSupplyingWarehouseCode(request.Role))
        {
            user.SupplyingWarehouseCode = request.SupplyingWarehouseCode?.Trim();

            // Optional, so only a positive id is taken — an unset picker posts 0, which would be a
            // foreign key to nothing.
            user.RouteId = request.RouteId is > 0 ? request.RouteId : null;
        }

        // The shop only. The three code columns are deliberately left null for this role: the shop is
        // where they come from, and copying them onto the account would give the same value two homes
        // that drift apart the moment the shop is edited.
        if (ApplicationRoles.RequiresShopAssignment(request.Role))
        {
            user.ShopId = request.ShopId;
        }

        user.FiscalDeviceId = fiscalDeviceId;

        context.Users.Add(user);
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "User {Username} created by {CreatorRole}",
            user.Username,
            currentUser.Role);

        try
        {
            await auditService.LogAsync(
                AuditActions.CreateUser,
                "User",
                user.Id.ToString(),
                $"User {request.Username} created with role {request.Role}",
                true);
        }
        catch
        {
        }

        return MapToUserDetailDto(user);
    }

    private static UserDetailDto MapToUserDetailDto(User user)
    {
        var permissions = new List<string>();
        if (!string.IsNullOrEmpty(user.Permissions))
        {
            try
            {
                permissions = JsonSerializer.Deserialize<List<string>>(user.Permissions) ?? new List<string>();
            }
            catch
            {
            }
        }

        return new UserDetailDto
        {
            Id = user.Id,
            Username = user.Username,
            Email = user.Email,
            FirstName = user.FirstName,
            LastName = user.LastName,
            Role = user.Role,
            IsActive = user.IsActive,
            EmailVerified = user.EmailVerified,
            TwoFactorEnabled = user.TwoFactorEnabled,
            IsLockedOut = user.LockoutEnd.HasValue && user.LockoutEnd > DateTime.UtcNow,
            LockoutEnd = user.LockoutEnd,
            Permissions = permissions,
            AssignedWarehouseCodes = user.GetWarehouseCodes(),
            AssignedSection = user.AssignedSection,
            AssignedCustomerCodes = user.GetCustomerCodes(),
            AssignedBusinessPartnerCode = user.AssignedBusinessPartnerCode,
            AssignedCostCentreCode = user.AssignedCostCentreCode,
            SupplyingWarehouseCode = user.SupplyingWarehouseCode,
            RouteId = user.RouteId,
            FiscalDeviceId = user.FiscalDeviceId,
            CreatedAt = user.CreatedAt,
            UpdatedAt = user.UpdatedAt,
            LastLoginAt = user.LastLoginAt
        };
    }
}
