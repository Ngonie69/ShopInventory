using ShopInventory.Web.Models;
using System.Net.Http.Json;

namespace ShopInventory.Web.Services;

// Local models referenced from UserManagement page
using UserFormModel = ShopInventory.Web.Components.Pages.UserManagement.UserFormModel;
using PermissionModel = ShopInventory.Web.Components.Pages.UserManagement.PermissionModel;
using SecurityEvent = ShopInventory.Web.Components.Pages.UserManagement.SecurityEvent;

/// <summary>
/// Interface for user management service
/// </summary>
public interface IUserManagementService
{
    Task<UserListResponse> GetUsersAsync(int page = 1, int pageSize = 20, string? search = null, string? role = null, string? status = null);
    Task<UserModel?> GetUserAsync(Guid id);
    Task CreateUserAsync(string username, string email, string password, string role);
    Task CreateUserAsync(UserFormModel model);
    Task UpdateUserAsync(Guid id, string email, string role);
    Task UpdateUserAsync(Guid id, UserFormModel model);
    Task DeleteUserAsync(Guid id);
    Task ChangePasswordAsync(Guid id, string newPassword);
    Task UnlockUserAsync(Guid id);
    Task ActivateUserAsync(Guid id);
    Task DeactivateUserAsync(Guid id);
    Task<List<string>> GetRolesAsync();

    // New permission-related methods
    Task<Dictionary<string, List<PermissionModel>>?> GetAvailablePermissionsAsync();
    Task UpdateUserPermissionsAsync(Guid userId, List<string> permissions);
    Task ResetTwoFactorAsync(Guid userId);
    Task<SecurityStats> GetSecurityStatsAsync();
}

/// <summary>
/// User management service implementation
/// </summary>
public class UserManagementService : IUserManagementService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<UserManagementService> _logger;

    public UserManagementService(HttpClient httpClient, ILogger<UserManagementService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<UserListResponse> GetUsersAsync(int page = 1, int pageSize = 20, string? search = null, string? role = null, string? status = null)
    {
        try
        {
            var url = $"api/user?page={page}&pageSize={pageSize}";
            if (!string.IsNullOrEmpty(search)) url += $"&search={Uri.EscapeDataString(search)}";
            if (!string.IsNullOrEmpty(role)) url += $"&role={role}";
            if (!string.IsNullOrEmpty(status)) url += $"&status={status}";
            return await _httpClient.GetFromJsonAsync<UserListResponse>(url) ?? new UserListResponse();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching users");
            return new UserListResponse();
        }
    }

    public async Task<UserModel?> GetUserAsync(Guid id)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<UserModel>($"api/user/{id}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching user {Id}", id);
            return null;
        }
    }

    public async Task CreateUserAsync(string username, string email, string password, string role)
    {
        var response = await _httpClient.PostAsJsonAsync("api/user", new CreateUserRequest
        {
            Username = username,
            Email = email,
            Password = password,
            Role = role
        });

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new Exception($"Failed to create user: {error}");
        }
    }

    public async Task UpdateUserAsync(Guid id, string email, string role)
    {
        var response = await _httpClient.PutAsJsonAsync($"api/user/{id}", new UpdateUserRequest
        {
            Email = email,
            Role = role
        });

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new Exception($"Failed to update user: {error}");
        }
    }

    public async Task DeleteUserAsync(Guid id)
    {
        var response = await _httpClient.DeleteAsync($"api/user/{id}");

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new Exception($"Failed to delete user: {error}");
        }
    }

    public async Task ChangePasswordAsync(Guid id, string newPassword)
    {
        var response = await _httpClient.PostAsJsonAsync($"api/user/{id}/change-password", new { NewPassword = newPassword });

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new Exception($"Failed to change password: {error}");
        }
    }

    public async Task UnlockUserAsync(Guid id)
    {
        var response = await _httpClient.PostAsync($"api/user/{id}/unlock", null);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new Exception($"Failed to unlock user: {error}");
        }
    }

    public async Task ActivateUserAsync(Guid id)
    {
        var response = await _httpClient.PostAsync($"api/user/{id}/activate", null);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new Exception($"Failed to activate user: {error}");
        }
    }

    public async Task DeactivateUserAsync(Guid id)
    {
        var response = await _httpClient.PostAsync($"api/user/{id}/deactivate", null);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new Exception($"Failed to deactivate user: {error}");
        }
    }

    public async Task<List<string>> GetRolesAsync()
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<List<string>>("api/user/roles") ?? new List<string>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching roles");
            return new List<string>();
        }
    }

    public async Task CreateUserAsync(UserFormModel model)
    {
        var response = await _httpClient.PostAsJsonAsync("api/usermanagement", new
        {
            Username = model.Username,
            Email = model.Email,
            Password = model.Password,
            FirstName = model.FirstName,
            LastName = model.LastName,
            Role = model.Role
        });

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new Exception($"Failed to create user: {error}");
        }
    }

    public async Task UpdateUserAsync(Guid id, UserFormModel model)
    {
        var response = await _httpClient.PutAsJsonAsync($"api/usermanagement/{id}", new
        {
            Email = model.Email,
            FirstName = model.FirstName,
            LastName = model.LastName,
            Role = model.Role,
            IsActive = model.IsActive
        });

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new Exception($"Failed to update user: {error}");
        }
    }

    public async Task<Dictionary<string, List<PermissionModel>>?> GetAvailablePermissionsAsync()
    {
        try
        {
            var response = await _httpClient.GetFromJsonAsync<AvailablePermissionsResponse>("api/usermanagement/permissions/available");
            return response?.PermissionsByCategory;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching available permissions");
            return null;
        }
    }

    public async Task UpdateUserPermissionsAsync(Guid userId, List<string> permissions)
    {
        var response = await _httpClient.PutAsJsonAsync($"api/usermanagement/{userId}/permissions", new
        {
            Permissions = permissions,
            ReplaceAll = true
        });

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new Exception($"Failed to update permissions: {error}");
        }
    }

    public async Task ResetTwoFactorAsync(Guid userId)
    {
        var response = await _httpClient.PostAsync($"api/usermanagement/{userId}/reset-2fa", null);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new Exception($"Failed to reset 2FA: {error}");
        }
    }

    public async Task<SecurityStats> GetSecurityStatsAsync()
    {
        try
        {
            // For now, return basic stats from user data
            var users = await GetUsersAsync(1, 1000);
            return new SecurityStats
            {
                UsersWithTwoFactor = users.Items?.Count(u => u.TwoFactorEnabled) ?? 0,
                UsersWithoutTwoFactor = users.Items?.Count(u => !u.TwoFactorEnabled) ?? 0,
                LockedUsers = users.Items?.Count(u => u.IsLocked) ?? 0,
                RecentEvents = new List<SecurityEvent>()
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching security stats");
            return new SecurityStats();
        }
    }
}

public class AvailablePermissionsResponse
{
    public Dictionary<string, List<PermissionModel>> PermissionsByCategory { get; set; } = new();
}

public class SecurityStats
{
    public int UsersWithTwoFactor { get; set; }
    public int UsersWithoutTwoFactor { get; set; }
    public int LockedUsers { get; set; }
    public List<SecurityEvent> RecentEvents { get; set; } = new();
}
