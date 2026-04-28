using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using ShopInventory.Common.Extensions;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;

namespace ShopInventory.Services;

/// <summary>
/// Service for user management operations
/// </summary>
public interface IUserManagementService
{
    /// <summary>
    /// Get all users with pagination
    /// </summary>
    Task<PagedResult<UserDetailDto>> GetUsersAsync(int page = 1, int pageSize = 10, string? search = null, string? role = null, bool? isActive = null);

    /// <summary>
    /// Get user by ID
    /// </summary>
    Task<UserDetailDto?> GetUserByIdAsync(Guid userId);

    /// <summary>
    /// Create a new user
    /// </summary>
    Task<ServiceResult<UserDetailDto>> CreateUserAsync(CreateUserDetailRequest request);

    /// <summary>
    /// Update a user
    /// </summary>
    Task<ServiceResult> UpdateUserAsync(Guid userId, UpdateUserDetailRequest request);

    /// <summary>
    /// Delete a user
    /// </summary>
    Task<ServiceResult> DeleteUserAsync(Guid userId);

    /// <summary>
    /// Get user permissions
    /// </summary>
    Task<UserPermissionsResponse?> GetUserPermissionsAsync(Guid userId);

    /// <summary>
    /// Update user permissions
    /// </summary>
    Task<ServiceResult> UpdateUserPermissionsAsync(Guid userId, UpdatePermissionsRequest request);

    /// <summary>
    /// Get all available permissions
    /// </summary>
    AvailablePermissionsResponse GetAvailablePermissions();

    /// <summary>
    /// Check if user has a specific permission
    /// </summary>
    Task<bool> HasPermissionAsync(Guid userId, string permission);

    /// <summary>
    /// Get effective permissions for a user (includes role defaults)
    /// </summary>
    Task<List<string>> GetEffectivePermissionsAsync(Guid userId);

    /// <summary>
    /// Unlock a user account
    /// </summary>
    Task<ServiceResult> UnlockUserAsync(Guid userId);

    /// <summary>
    /// Reset user's 2FA
    /// </summary>
    Task<ServiceResult> ResetTwoFactorAsync(Guid userId);
}

/// <summary>
/// Implementation of user management service
/// </summary>
public class UserManagementService : IUserManagementService
{
    private readonly ApplicationDbContext _context;
    private readonly IMemoryCache _memoryCache;
    private readonly ILogger<UserManagementService> _logger;
    private static readonly TimeSpan EffectivePermissionsCacheDuration = TimeSpan.FromMinutes(5);
    private const string EffectivePermissionsCacheKeyPrefix = "user-permissions:";

    public UserManagementService(ApplicationDbContext context, IMemoryCache memoryCache, ILogger<UserManagementService> logger)
    {
        _context = context;
        _memoryCache = memoryCache;
        _logger = logger;
    }

    public async Task<PagedResult<UserDetailDto>> GetUsersAsync(int page = 1, int pageSize = 10, string? search = null, string? role = null, bool? isActive = null)
    {
        var query = _context.Users.AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search}%";
            query = query.Where(u =>
                EF.Functions.ILike(u.Username, pattern) ||
                (u.Email != null && EF.Functions.ILike(u.Email, pattern)) ||
                (u.FirstName != null && EF.Functions.ILike(u.FirstName, pattern)) ||
                (u.LastName != null && EF.Functions.ILike(u.LastName, pattern)));
        }

        if (!string.IsNullOrWhiteSpace(role))
        {
            query = query.Where(u => u.Role == role);
        }

        if (isActive.HasValue)
        {
            query = query.Where(u => u.IsActive == isActive.Value);
        }

        var totalCount = await query.CountAsync();

        var entities = await query
            .OrderBy(u => u.Username)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        foreach (var e in entities.Where(u => u.Role == "Merchandiser"))
        {
            _logger.LogInformation("GetUsersAsync - {User} raw AssignedCustomerCodes: {Raw}", e.Username, e.AssignedCustomerCodes ?? "NULL");
        }

        var users = entities.Select(u => MapToUserDetailDto(u)).ToList();

        return new PagedResult<UserDetailDto>
        {
            Items = users,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    public async Task<UserDetailDto?> GetUserByIdAsync(Guid userId)
    {
        var user = await _context.Users.FindAsync(userId);
        return user == null ? null : MapToUserDetailDto(user);
    }

    public async Task<ServiceResult<UserDetailDto>> CreateUserAsync(CreateUserDetailRequest request)
    {
        // Check for existing username
        if (await _context.Users.WhereUsernameMatches(request.Username).AnyAsync())
        {
            return ServiceResult<UserDetailDto>.Failure("Username already exists");
        }

        // Check for existing email
        if (!string.IsNullOrWhiteSpace(request.Email) && await _context.Users.WhereEmailMatches(request.Email).AnyAsync())
        {
            return ServiceResult<UserDetailDto>.Failure("Email already exists");
        }

        // Validate role
        var validRoles = new[] { "Admin", "Manager", "User", "ReadOnly", "Cashier", "StockController", "DepotController", "PodOperator", "Driver", "Merchandiser", "SalesRep", "MerchandiserPurchaseOrderViewer" };
        if (!validRoles.Contains(request.Role))
        {
            return ServiceResult<UserDetailDto>.Failure($"Invalid role. Valid roles: {string.Join(", ", validRoles)}");
        }

        // Validate warehouse assignment for StockController/DepotController roles
        if ((request.Role == "StockController" || request.Role == "DepotController") && (request.AssignedWarehouseCodes == null || request.AssignedWarehouseCodes.Count == 0))
        {
            return ServiceResult<UserDetailDto>.Failure($"At least one assigned warehouse code is required for {request.Role} role");
        }

        // Validate customer assignment for Merchandiser role
        if (request.Role == "Merchandiser" && (request.AssignedCustomerCodes == null || request.AssignedCustomerCodes.Count == 0))
        {
            return ServiceResult<UserDetailDto>.Failure("At least one assigned customer code is required for Merchandiser role");
        }

        // Validate section assignment for Driver role
        if (request.Role == "Driver" && string.IsNullOrWhiteSpace(request.AssignedSection))
        {
            return ServiceResult<UserDetailDto>.Failure("An assigned section is required for Driver role");
        }

        // Determine permissions
        List<string> permissions;
        if (request.Permissions != null && request.Permissions.Count > 0)
        {
            // Validate permissions
            var allPermissions = Permission.GetAllPermissions();
            var invalidPermissions = request.Permissions.Except(allPermissions).ToList();
            if (invalidPermissions.Count > 0)
            {
                return ServiceResult<UserDetailDto>.Failure($"Invalid permissions: {string.Join(", ", invalidPermissions)}");
            }
            permissions = request.Permissions;
        }
        else
        {
            // Use role defaults
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
            Role = request.Role,
            IsActive = true,
            EmailVerified = false,
            TwoFactorEnabled = false,
            Permissions = JsonSerializer.Serialize(permissions),
            CreatedAt = DateTime.UtcNow
        };

        if (request.Role == "StockController" || request.Role == "DepotController" || request.Role == "Merchandiser")
            user.SetWarehouseCodes(request.AssignedWarehouseCodes);

        if (request.Role == "Merchandiser")
            user.SetCustomerCodes(request.AssignedCustomerCodes);

        if (request.Role == "Driver")
            user.AssignedSection = request.AssignedSection;

        if (request.AllowedPaymentMethods != null && request.AllowedPaymentMethods.Count > 0)
            user.SetAllowedPaymentMethods(request.AllowedPaymentMethods);

        if (!string.IsNullOrWhiteSpace(request.DefaultGLAccount))
            user.DefaultGLAccount = request.DefaultGLAccount;

        if (request.AllowedPaymentBusinessPartners != null && request.AllowedPaymentBusinessPartners.Count > 0)
            user.SetAllowedPaymentBusinessPartners(request.AllowedPaymentBusinessPartners);

        _context.Users.Add(user);
        await _context.SaveChangesAsync();
        InvalidateEffectivePermissionsCache(user.Id);

        _logger.LogInformation("User {Username} created by admin", user.Username);

        var dto = MapToUserDetailDto(user);
        return ServiceResult<UserDetailDto>.Success(dto, "User created successfully");
    }

    public async Task<ServiceResult> UpdateUserAsync(Guid userId, UpdateUserDetailRequest request)
    {
        var user = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null)
        {
            return ServiceResult.Failure("User not found");
        }

        // Update email if provided
        if (!string.IsNullOrWhiteSpace(request.Email) && !string.Equals(request.Email, user.Email, StringComparison.OrdinalIgnoreCase))
        {
            if (await _context.Users
                .Where(u => u.Id != userId)
                .WhereEmailMatches(request.Email)
                .AnyAsync())
            {
                return ServiceResult.Failure("Email already exists");
            }
            user.Email = request.Email;
            user.EmailVerified = false;
        }

        // Update other fields
        if (request.FirstName != null) user.FirstName = request.FirstName;
        if (request.LastName != null) user.LastName = request.LastName;
        if (request.IsActive.HasValue) user.IsActive = request.IsActive.Value;

        // Update role if provided
        if (!string.IsNullOrWhiteSpace(request.Role))
        {
            var validRoles = new[] { "Admin", "Manager", "User", "ReadOnly", "Cashier", "StockController", "DepotController", "PodOperator", "Driver", "Merchandiser", "SalesRep", "MerchandiserPurchaseOrderViewer" };
            if (!validRoles.Contains(request.Role))
            {
                return ServiceResult.Failure($"Invalid role. Valid roles: {string.Join(", ", validRoles)}");
            }
            user.Role = request.Role;
        }

        // Update assigned warehouses
        if (request.AssignedWarehouseCodes != null)
        {
            if (user.Role == "StockController" || user.Role == "DepotController" || user.Role == "Merchandiser")
                user.SetWarehouseCodes(request.AssignedWarehouseCodes);
            else
                user.SetWarehouseCodes(null);
        }

        // Update assigned customers
        if (request.AssignedCustomerCodes != null)
        {
            if (user.Role == "Merchandiser")
            {
                _logger.LogInformation("Setting customer codes for {User}: {Codes}", user.Username, string.Join(",", request.AssignedCustomerCodes));
                user.SetCustomerCodes(request.AssignedCustomerCodes);
                _logger.LogInformation("After SetCustomerCodes, raw value: {Raw}", user.AssignedCustomerCodes ?? "NULL");
            }
            else
                user.SetCustomerCodes(null);
        }

        // Update assigned section
        if (user.Role == "Driver")
            user.AssignedSection = request.AssignedSection;
        else
            user.AssignedSection = null;

        // Update allowed payment methods
        if (request.AllowedPaymentMethods != null)
        {
            user.SetAllowedPaymentMethods(request.AllowedPaymentMethods);
        }

        // Update default GL account
        if (request.DefaultGLAccount != null)
        {
            user.DefaultGLAccount = string.IsNullOrWhiteSpace(request.DefaultGLAccount) ? null : request.DefaultGLAccount;
        }

        // Update allowed payment business partners
        if (request.AllowedPaymentBusinessPartners != null)
        {
            user.SetAllowedPaymentBusinessPartners(request.AllowedPaymentBusinessPartners);
        }

        // Validate warehouses are set for warehouse-dependent roles
        if ((user.Role == "StockController" || user.Role == "DepotController") && user.GetWarehouseCodes().Count == 0)
        {
            return ServiceResult.Failure($"At least one assigned warehouse code is required for {user.Role} role");
        }

        // Validate customers are set for Merchandiser
        if (user.Role == "Merchandiser" && user.GetCustomerCodes().Count == 0)
        {
            return ServiceResult.Failure("At least one assigned customer code is required for Merchandiser role");
        }

        // Validate section is set for Driver
        if (user.Role == "Driver" && string.IsNullOrWhiteSpace(user.AssignedSection))
        {
            return ServiceResult.Failure("An assigned section is required for Driver role");
        }

        // Update permissions if provided
        if (request.Permissions != null)
        {
            if (request.Permissions.Count == 0)
            {
                // Reset to role defaults
                user.Permissions = JsonSerializer.Serialize(Permission.GetDefaultPermissionsForRole(user.Role));
            }
            else
            {
                // Validate permissions
                var allPermissions = Permission.GetAllPermissions();
                var invalidPermissions = request.Permissions.Except(allPermissions).ToList();
                if (invalidPermissions.Count > 0)
                {
                    return ServiceResult.Failure($"Invalid permissions: {string.Join(", ", invalidPermissions)}");
                }
                user.Permissions = JsonSerializer.Serialize(request.Permissions);
            }
        }

        user.UpdatedAt = DateTime.UtcNow;

        // Use ExecuteUpdateAsync — direct SQL UPDATE, bypasses EF change tracking entirely
        var rowsAffected = await _context.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(u => u
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
                .SetProperty(x => x.UpdatedAt, user.UpdatedAt));

        _logger.LogInformation("UpdateUser ExecuteUpdate for user {UserId}: {RowsAffected} rows affected", userId, rowsAffected);

        if (rowsAffected == 0)
        {
            _logger.LogError("UpdateUser wrote 0 rows for user {UserId}", userId);
            return ServiceResult.Failure("Update failed: no rows were modified. Please try again.");
        }

        InvalidateEffectivePermissionsCache(userId);
        _logger.LogInformation("User {UserId} updated", userId);

        return ServiceResult.Success("User updated successfully");
    }

    public async Task<ServiceResult> DeleteUserAsync(Guid userId)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null)
        {
            return ServiceResult.Failure("User not found");
        }

        // Soft delete - just deactivate
        user.IsActive = false;
        user.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        InvalidateEffectivePermissionsCache(userId);

        _logger.LogInformation("User {UserId} deactivated", userId);

        return ServiceResult.Success("User deactivated successfully");
    }

    public async Task<UserPermissionsResponse?> GetUserPermissionsAsync(Guid userId)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null) return null;

        var directPermissions = new List<string>();
        if (!string.IsNullOrEmpty(user.Permissions))
        {
            try
            {
                directPermissions = JsonSerializer.Deserialize<List<string>>(user.Permissions) ?? new List<string>();
            }
            catch
            {
                // Ignore deserialization errors
            }
        }

        var effectivePermissions = await GetEffectivePermissionsAsync(userId);

        return new UserPermissionsResponse
        {
            UserId = user.Id,
            Username = user.Username,
            Role = user.Role,
            Permissions = directPermissions,
            EffectivePermissions = effectivePermissions
        };
    }

    public async Task<ServiceResult> UpdateUserPermissionsAsync(Guid userId, UpdatePermissionsRequest request)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null)
        {
            return ServiceResult.Failure("User not found");
        }

        // Validate permissions
        var allPermissions = Permission.GetAllPermissions();
        var invalidPermissions = request.Permissions.Except(allPermissions).ToList();
        if (invalidPermissions.Count > 0)
        {
            return ServiceResult.Failure($"Invalid permissions: {string.Join(", ", invalidPermissions)}");
        }

        List<string> newPermissions;
        if (request.ReplaceAll)
        {
            newPermissions = request.Permissions;
        }
        else
        {
            // Merge with existing
            var existing = new List<string>();
            if (!string.IsNullOrEmpty(user.Permissions))
            {
                try
                {
                    existing = JsonSerializer.Deserialize<List<string>>(user.Permissions) ?? new List<string>();
                }
                catch { }
            }
            newPermissions = existing.Union(request.Permissions).ToList();
        }

        user.Permissions = JsonSerializer.Serialize(newPermissions);
        user.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        InvalidateEffectivePermissionsCache(userId);

        _logger.LogInformation("Permissions updated for user {UserId}", userId);

        return ServiceResult.Success("Permissions updated successfully");
    }

    public AvailablePermissionsResponse GetAvailablePermissions()
    {
        var grouped = Permission.GetAllPermissionsGrouped();
        var response = new AvailablePermissionsResponse();

        foreach (var category in grouped)
        {
            response.PermissionsByCategory[category.Key] = category.Value
                .Select(p => new PermissionDto
                {
                    Code = p.Code,
                    Name = p.Name,
                    Description = p.Description
                })
                .ToList();
        }

        return response;
    }

    public async Task<bool> HasPermissionAsync(Guid userId, string permission)
    {
        var effectivePermissions = await GetEffectivePermissionsAsync(userId);
        return effectivePermissions.Contains(permission);
    }

    public async Task<List<string>> GetEffectivePermissionsAsync(Guid userId)
    {
        var cacheKey = GetEffectivePermissionsCacheKey(userId);
        if (_memoryCache.TryGetValue(cacheKey, out List<string>? cachedPermissions) && cachedPermissions != null)
        {
            return cachedPermissions;
        }

        var user = await _context.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new
            {
                u.Role,
                u.Permissions,
                u.IsActive
            })
            .FirstOrDefaultAsync();

        if (user == null || !user.IsActive)
        {
            return new List<string>();
        }

        var permissions = new HashSet<string>();

        // Add role-based permissions
        var rolePermissions = Permission.GetDefaultPermissionsForRole(user.Role);
        foreach (var p in rolePermissions)
        {
            permissions.Add(p);
        }

        // Add direct permissions
        if (!string.IsNullOrEmpty(user.Permissions))
        {
            try
            {
                var directPermissions = JsonSerializer.Deserialize<List<string>>(user.Permissions);
                if (directPermissions != null)
                {
                    foreach (var p in directPermissions)
                    {
                        permissions.Add(p);
                    }
                }
            }
            catch
            {
                // Ignore deserialization errors
            }
        }

        var effectivePermissions = permissions.ToList();
        _memoryCache.Set(cacheKey, effectivePermissions, EffectivePermissionsCacheDuration);

        return effectivePermissions;
    }

    public async Task<ServiceResult> UnlockUserAsync(Guid userId)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null)
        {
            return ServiceResult.Failure("User not found");
        }

        user.FailedLoginAttempts = 0;
        user.LockoutEnd = null;
        user.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        InvalidateEffectivePermissionsCache(userId);

        _logger.LogInformation("User {UserId} unlocked", userId);

        return ServiceResult.Success("User account unlocked successfully");
    }

    public async Task<ServiceResult> ResetTwoFactorAsync(Guid userId)
    {
        var user = await _context.Users.FindAsync(userId);
        if (user == null)
        {
            return ServiceResult.Failure("User not found");
        }

        user.TwoFactorEnabled = false;
        user.TwoFactorSecret = null;
        user.TwoFactorBackupCodes = null;
        user.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        InvalidateEffectivePermissionsCache(userId);

        _logger.LogInformation("2FA reset for user {UserId}", userId);

        return ServiceResult.Success("Two-factor authentication reset successfully");
    }

    #region Private Methods

    private static string GetEffectivePermissionsCacheKey(Guid userId)
    {
        return $"{EffectivePermissionsCacheKeyPrefix}{userId}";
    }

    private void InvalidateEffectivePermissionsCache(Guid userId)
    {
        _memoryCache.Remove(GetEffectivePermissionsCacheKey(userId));
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
            catch { }
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
            AllowedPaymentMethods = user.GetAllowedPaymentMethods(),
            DefaultGLAccount = user.DefaultGLAccount,
            AllowedPaymentBusinessPartners = user.GetAllowedPaymentBusinessPartners(),
            AssignedSection = user.AssignedSection,
            AssignedCustomerCodes = user.GetCustomerCodes(),
            CreatedAt = user.CreatedAt,
            UpdatedAt = user.UpdatedAt,
            LastLoginAt = user.LastLoginAt
        };
    }

    private static string FormatPermissionName(string permission)
    {
        // Convert camelCase to Title Case with spaces
        var result = string.Concat(permission.Select((c, i) =>
            i > 0 && char.IsUpper(c) ? " " + c : c.ToString()));
        return result.Replace(".", " - ");
    }

    private static string GetPermissionDescription(string permission)
    {
        return permission switch
        {
            Permission.ViewDashboard => "View the main dashboard",
            Permission.ViewProducts => "View product listings",
            Permission.CreateProducts => "Create new products",
            Permission.EditProducts => "Edit existing products",
            Permission.DeleteProducts => "Delete products",
            Permission.ManageProductPrices => "Manage product prices",
            Permission.ViewInvoices => "View invoices",
            Permission.CreateInvoices => "Create new invoices",
            Permission.EditInvoices => "Edit existing invoices",
            Permission.DeleteInvoices => "Delete invoices",
            Permission.VoidInvoices => "Void invoices",
            Permission.ViewPayments => "View payments",
            Permission.CreatePayments => "Create new payments",
            Permission.ProcessRefunds => "Process payment refunds",
            Permission.ViewInventory => "View inventory levels",
            Permission.AdjustInventory => "Make inventory adjustments",
            Permission.TransferInventory => "Transfer inventory between locations",
            Permission.ViewReports => "View reports",
            Permission.ExportReports => "Export reports",
            Permission.ViewCustomers => "View customer information",
            Permission.CreateCustomers => "Create new customers",
            Permission.EditCustomers => "Edit customer information",
            Permission.DeleteCustomers => "Delete customers",
            Permission.ViewUsers => "View user accounts",
            Permission.CreateUsers => "Create new user accounts",
            Permission.EditUsers => "Edit user accounts",
            Permission.DeleteUsers => "Delete user accounts",
            Permission.ManageUserRoles => "Assign roles to users",
            Permission.ManageUserPermissions => "Manage user permissions",
            Permission.ViewSettings => "View system settings",
            Permission.ManageSettings => "Manage system settings",
            Permission.ManageIntegrations => "Manage third-party integrations",
            Permission.ViewAuditLogs => "View audit logs",
            Permission.ExportAuditLogs => "Export audit logs",
            Permission.ViewWebhooks => "View webhooks",
            Permission.ManageWebhooks => "Create and manage webhooks",
            Permission.SystemAdmin => "Full system administration access",
            Permission.ManageBackups => "Manage system backups",
            _ => permission
        };
    }

    #endregion
}

/// <summary>
/// Generic service result with data
/// </summary>
public class ServiceResult<T>
{
    public bool IsSuccess { get; set; }
    public string Message { get; set; } = string.Empty;
    public T? Data { get; set; }
    public List<string> Errors { get; set; } = new();

    public static ServiceResult<T> Success(T data, string message = "Operation completed successfully")
    {
        return new ServiceResult<T> { IsSuccess = true, Message = message, Data = data };
    }

    public static ServiceResult<T> Failure(string error)
    {
        return new ServiceResult<T> { IsSuccess = false, Message = error, Errors = new List<string> { error } };
    }
}

/// <summary>
/// Paged result wrapper
/// </summary>
public class PagedResult<T>
{
    public List<T> Items { get; set; } = new();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages => (int)Math.Ceiling(TotalCount / (double)PageSize);
}
