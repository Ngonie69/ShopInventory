using Blazored.LocalStorage;
using ShopInventory.Web.Data;
using ShopInventory.Web.Models;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

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
    Task<(List<string> DirectPermissions, List<string> EffectivePermissions, bool UsesRoleDefaults)> GetUserPermissionsAsync(Guid id);
    Task CreateUserAsync(string username, string email, string password, string role);
    Task CreateUserAsync(UserFormModel model);
    Task CreateMerchandiserAccountAsync(CreateMerchandiserAccountFormModel model, CancellationToken cancellationToken = default);
    Task<List<ManagedMerchandiserAccountModel>> GetManagedMerchandiserAccountsAsync(CancellationToken cancellationToken = default);
    Task UpdateMerchandiserAssignedCustomersAsync(Guid userId, IReadOnlyCollection<string> assignedWarehouseCodes, IReadOnlyCollection<string> assignedCustomerCodes, CancellationToken cancellationToken = default);
    Task<int> UpdateAllDriverAssignedCustomersAsync(IReadOnlyCollection<string> assignedCustomerCodes, CancellationToken cancellationToken = default);
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
    private const int AuthTokenReadAttempts = 3;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILocalStorageService _localStorage;
    private readonly IAppSettingsService _appSettingsService;
    private readonly ILogger<UserManagementService> _logger;
    private string? _cachedAccessToken;

    public UserManagementService(
        IHttpClientFactory httpClientFactory,
        ILocalStorageService localStorage,
        IAppSettingsService appSettingsService,
        ILogger<UserManagementService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _localStorage = localStorage;
        _appSettingsService = appSettingsService;
        _logger = logger;
    }

    public async Task<UserListResponse> GetUsersAsync(int page = 1, int pageSize = 20, string? search = null, string? role = null, string? status = null)
    {
        var client = await CreateAuthenticatedClientAsync();
        var url = $"api/usermanagement?page={page}&pageSize={pageSize}";
        if (!string.IsNullOrEmpty(search)) url += $"&search={Uri.EscapeDataString(search)}";
        if (!string.IsNullOrEmpty(role)) url += $"&role={role}";
        if (!string.IsNullOrEmpty(status))
        {
            if (status == "active") url += "&isActive=true";
            else if (status == "inactive") url += "&isActive=false";
        }

        var response = await client.GetAsync(url);

        if (!response.IsSuccessStatusCode)
        {
            await ThrowApiExceptionAsync(response, "Failed to load users.");
        }

        return await response.Content.ReadFromJsonAsync<UserListResponse>() ?? new UserListResponse();
    }

    public async Task<UserModel?> GetUserAsync(Guid id)
    {
        try
        {
            var client = await CreateAuthenticatedClientAsync();
            return await client.GetFromJsonAsync<UserModel>($"api/user/{id}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching user {Id}", id);
            return null;
        }
    }

    public async Task<(List<string> DirectPermissions, List<string> EffectivePermissions, bool UsesRoleDefaults)> GetUserPermissionsAsync(Guid id)
    {
        var client = await CreateAuthenticatedClientAsync();
        var response = await client.GetAsync($"api/usermanagement/{id}/permissions");

        if (!response.IsSuccessStatusCode)
        {
            await ThrowApiExceptionAsync(response, "Failed to load user permissions.");
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        return (
            ReadStringListProperty(root, "permissions"),
            ReadStringListProperty(root, "effectivePermissions"),
            ReadBooleanProperty(root, "usesRoleDefaults"));
    }

    public async Task CreateUserAsync(string username, string email, string password, string role)
    {
        var client = await CreateAuthenticatedClientAsync();
        var response = await client.PostAsJsonAsync("api/user", new CreateUserRequest
        {
            Username = username,
            Email = email,
            Password = password,
            Role = role
        });

        if (!response.IsSuccessStatusCode)
        {
            await ThrowApiExceptionAsync(response, "Failed to create user.");
        }
    }

    public async Task UpdateUserAsync(Guid id, string email, string role)
    {
        var client = await CreateAuthenticatedClientAsync();
        var response = await client.PutAsJsonAsync($"api/user/{id}", new UpdateUserRequest
        {
            Email = email,
            Role = role
        });

        if (!response.IsSuccessStatusCode)
        {
            await ThrowApiExceptionAsync(response, "Failed to update user.");
        }
    }

    public async Task DeleteUserAsync(Guid id)
    {
        var client = await CreateAuthenticatedClientAsync();
        var response = await client.DeleteAsync($"api/user/{id}");

        if (!response.IsSuccessStatusCode)
        {
            await ThrowApiExceptionAsync(response, "Failed to delete user.");
        }
    }

    public async Task ChangePasswordAsync(Guid id, string newPassword)
    {
        var client = await CreateAuthenticatedClientAsync();
        var response = await client.PostAsJsonAsync($"api/user/{id}/change-password", new { NewPassword = newPassword });

        if (!response.IsSuccessStatusCode)
        {
            await ThrowApiExceptionAsync(response, "Failed to change password.");
        }
    }

    public async Task UnlockUserAsync(Guid id)
    {
        var client = await CreateAuthenticatedClientAsync();
        var response = await client.PostAsync($"api/user/{id}/unlock", null);

        if (!response.IsSuccessStatusCode)
        {
            await ThrowApiExceptionAsync(response, "Failed to unlock user.");
        }
    }

    public async Task ActivateUserAsync(Guid id)
    {
        var client = await CreateAuthenticatedClientAsync();
        var response = await client.PostAsync($"api/user/{id}/activate", null);

        if (!response.IsSuccessStatusCode)
        {
            await ThrowApiExceptionAsync(response, "Failed to activate user.");
        }
    }

    public async Task DeactivateUserAsync(Guid id)
    {
        var client = await CreateAuthenticatedClientAsync();
        var response = await client.PostAsync($"api/user/{id}/deactivate", null);

        if (!response.IsSuccessStatusCode)
        {
            await ThrowApiExceptionAsync(response, "Failed to deactivate user.");
        }
    }

    public async Task<List<string>> GetRolesAsync()
    {
        try
        {
            var client = await CreateAuthenticatedClientAsync();
            return await client.GetFromJsonAsync<List<string>>("api/user/roles") ?? new List<string>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching roles");
            return new List<string>();
        }
    }

    public async Task CreateUserAsync(UserFormModel model)
    {
        var assignedCustomerCodes = await GetDriverAssignedCustomerCodesForRequestAsync(model.Role, model.AssignedCustomerCodes);

        var client = await CreateAuthenticatedClientAsync();
        var response = await client.PostAsJsonAsync("api/usermanagement", new
        {
            Username = model.Username,
            Email = model.Email,
            Password = model.Password,
            FirstName = model.FirstName,
            LastName = model.LastName,
            Role = model.Role,
            AssignedWarehouseCodes = model.AssignedWarehouseCodes,
            AllowedPaymentMethods = model.AllowedPaymentMethods,
            DefaultGLAccount = model.DefaultGLAccount,
            AllowedPaymentBusinessPartners = model.AllowedPaymentBusinessPartners,
            AssignedSection = model.AssignedSection,
            AssignedCustomerCodes = assignedCustomerCodes
        });

        if (!response.IsSuccessStatusCode)
        {
            await ThrowApiExceptionAsync(response, "Failed to create user.");
        }
    }

    public async Task CreateMerchandiserAccountAsync(CreateMerchandiserAccountFormModel model, CancellationToken cancellationToken = default)
    {
        var client = await CreateAuthenticatedClientAsync();
        var response = await client.PostAsJsonAsync("api/usermanagement", new
        {
            Username = model.Username,
            Email = model.Email,
            Password = model.Password,
            FirstName = model.FirstName,
            LastName = model.LastName,
            Role = "Merchandiser",
            AssignedWarehouseCodes = model.AssignedWarehouseCodes,
            AssignedCustomerCodes = model.AssignedCustomerCodes
        }, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            return;
        }

        await ThrowApiExceptionAsync(response, "Failed to create merchandiser account.");
    }

    public async Task<List<ManagedMerchandiserAccountModel>> GetManagedMerchandiserAccountsAsync(CancellationToken cancellationToken = default)
    {
        var client = await CreateAuthenticatedClientAsync();
        var response = await client.GetAsync("api/usermanagement/merchandisers", cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<List<ManagedMerchandiserAccountModel>>(cancellationToken: cancellationToken)
                ?? new List<ManagedMerchandiserAccountModel>();
        }

        await ThrowApiExceptionAsync(response, "Failed to load merchandiser accounts.");
        return new List<ManagedMerchandiserAccountModel>();
    }

    public async Task UpdateMerchandiserAssignedCustomersAsync(
        Guid userId,
        IReadOnlyCollection<string> assignedWarehouseCodes,
        IReadOnlyCollection<string> assignedCustomerCodes,
        CancellationToken cancellationToken = default)
    {
        var client = await CreateAuthenticatedClientAsync();
        var response = await client.PutAsJsonAsync(
            $"api/usermanagement/merchandisers/{userId}/assigned-customers",
            new
            {
                AssignedWarehouseCodes = assignedWarehouseCodes,
                AssignedCustomerCodes = assignedCustomerCodes
            },
            cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            return;
        }

        await ThrowApiExceptionAsync(response, "Failed to update assigned customers.");
    }

    public async Task<int> UpdateAllDriverAssignedCustomersAsync(
        IReadOnlyCollection<string> assignedCustomerCodes,
        CancellationToken cancellationToken = default)
    {
        var client = await CreateAuthenticatedClientAsync();
        var response = await client.PutAsJsonAsync(
            "api/usermanagement/drivers/assigned-customers",
            new
            {
                AssignedCustomerCodes = assignedCustomerCodes
            },
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            await ThrowApiExceptionAsync(response, "Failed to update driver business partners.");
        }

        var payload = await response.Content.ReadFromJsonAsync<DriverAssignedCustomerUpdateResponse>(cancellationToken: cancellationToken);
        return payload?.UpdatedDriverCount ?? 0;
    }

    public async Task UpdateUserAsync(Guid id, UserFormModel model)
    {
        var assignedCustomerCodes = await GetDriverAssignedCustomerCodesForRequestAsync(model.Role, model.AssignedCustomerCodes);

        var client = await CreateAuthenticatedClientAsync();
        var response = await client.PutAsJsonAsync($"api/usermanagement/{id}", new
        {
            Username = model.Username,
            Email = model.Email,
            FirstName = model.FirstName,
            LastName = model.LastName,
            Role = model.Role,
            IsActive = model.IsActive,
            AssignedWarehouseCodes = model.AssignedWarehouseCodes,
            AllowedPaymentMethods = model.AllowedPaymentMethods,
            DefaultGLAccount = model.DefaultGLAccount,
            AllowedPaymentBusinessPartners = model.AllowedPaymentBusinessPartners,
            AssignedSection = model.AssignedSection,
            AssignedCustomerCodes = assignedCustomerCodes
        });

        if (!response.IsSuccessStatusCode)
        {
            await ThrowApiExceptionAsync(response, "Failed to update user.");
        }
    }

    private async Task<List<string>> GetDriverAssignedCustomerCodesForRequestAsync(string role, List<string> assignedCustomerCodes)
    {
        if (!string.Equals(role, "Driver", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(role, "PodOperator", StringComparison.OrdinalIgnoreCase))
        {
            return assignedCustomerCodes;
        }

        var settingValue = await _appSettingsService.GetValueAsync(SettingKeys.DriverVisibleBusinessPartners);
        if (string.IsNullOrWhiteSpace(settingValue))
        {
            return new List<string>();
        }

        try
        {
            return (JsonSerializer.Deserialize<List<string>>(settingValue) ?? new List<string>())
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Select(code => code.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(code => code)
                .ToList();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "DriverVisibleBusinessPartners setting contained invalid JSON");
            return new List<string>();
        }
    }

    public async Task<Dictionary<string, List<PermissionModel>>?> GetAvailablePermissionsAsync()
    {
        try
        {
            var client = await CreateAuthenticatedClientAsync();
            var response = await client.GetFromJsonAsync<AvailablePermissionsResponse>("api/usermanagement/permissions/available");
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
        var client = await CreateAuthenticatedClientAsync();
        var response = await client.PutAsJsonAsync($"api/usermanagement/{userId}/permissions", new
        {
            Permissions = permissions,
            ReplaceAll = true
        });

        if (!response.IsSuccessStatusCode)
        {
            await ThrowApiExceptionAsync(response, "Failed to update permissions.");
        }
    }

    public async Task ResetTwoFactorAsync(Guid userId)
    {
        var client = await CreateAuthenticatedClientAsync();
        var response = await client.PostAsync($"api/usermanagement/{userId}/reset-2fa", null);

        if (!response.IsSuccessStatusCode)
        {
            await ThrowApiExceptionAsync(response, "Failed to reset 2FA.");
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

    private async Task<HttpClient> CreateAuthenticatedClientAsync()
    {
        var client = _httpClientFactory.CreateClient("ShopInventoryApiUser");

        if (!string.IsNullOrWhiteSpace(_cachedAccessToken))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _cachedAccessToken);
        }

        Exception? lastException = null;

        for (var attempt = 1; attempt <= AuthTokenReadAttempts; attempt++)
        {
            try
            {
                var token = await _localStorage.GetItemAsync<string>("authToken");
                if (string.IsNullOrWhiteSpace(token))
                {
                    lastException = new HttpRequestException(
                        "Authentication failed. Please log out and log in again.",
                        null,
                        HttpStatusCode.Unauthorized);
                }
                else
                {
                    _cachedAccessToken = token;
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    return client;
                }
            }
            catch (Exception ex) when (ex is not HttpRequestException)
            {
                lastException = ex;
                _logger.LogDebug(ex, "Could not access auth token for user management API call on attempt {Attempt}", attempt);
            }

            if (attempt < AuthTokenReadAttempts)
            {
                await Task.Delay(150);
            }
        }

        if (!string.IsNullOrWhiteSpace(_cachedAccessToken))
        {
            _logger.LogWarning(lastException, "Falling back to cached auth token for user management API call");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _cachedAccessToken);
            return client;
        }

        try
        {
            throw lastException ?? new HttpRequestException(
                "Authentication failed. Please log out and log in again.",
                null,
                HttpStatusCode.Unauthorized);
        }
        catch (HttpRequestException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not access auth token for user management API call");
            throw new HttpRequestException(
                "Authentication failed. Please log out and log in again.",
                null,
                HttpStatusCode.Unauthorized);
        }
    }

    private static async Task ThrowApiExceptionAsync(HttpResponseMessage response, string fallbackMessage)
    {
        var errorBody = await response.Content.ReadAsStringAsync();
        throw new HttpRequestException(
            ExtractApiErrorMessage(errorBody, response.StatusCode, fallbackMessage),
            null,
            response.StatusCode);
    }

    private static List<string> ReadStringListProperty(JsonElement root, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(root, propertyName, out var propertyElement) || propertyElement.ValueKind != JsonValueKind.Array)
        {
            return new List<string>();
        }

        return propertyElement.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool ReadBooleanProperty(JsonElement root, string propertyName)
    {
        return TryGetPropertyIgnoreCase(root, propertyName, out var propertyElement)
            && (propertyElement.ValueKind == JsonValueKind.True || propertyElement.ValueKind == JsonValueKind.False)
            && propertyElement.GetBoolean();
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement root, string propertyName, out JsonElement propertyElement)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    propertyElement = property.Value;
                    return true;
                }
            }
        }

        propertyElement = default;
        return false;
    }

    private static string ExtractApiErrorMessage(
        string errorBody,
        HttpStatusCode? statusCode = null,
        string? fallbackMessage = null)
    {
        string? extractedMessage = null;

        if (!string.IsNullOrWhiteSpace(errorBody))
        {
            try
            {
                using var document = JsonDocument.Parse(errorBody);
                var root = document.RootElement;

                if (root.TryGetProperty("errors", out var errorsElement) && errorsElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in errorsElement.EnumerateObject())
                    {
                        if (property.Value.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var error in property.Value.EnumerateArray())
                            {
                                var message = error.GetString();
                                if (!string.IsNullOrWhiteSpace(message))
                                {
                                    extractedMessage = message;
                                    break;
                                }
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(extractedMessage))
                        {
                            break;
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(extractedMessage) && root.TryGetProperty("title", out var titleElement))
                {
                    var title = titleElement.GetString();
                    if (!string.IsNullOrWhiteSpace(title))
                    {
                        extractedMessage = title;
                    }
                }

                if (string.IsNullOrWhiteSpace(extractedMessage) && root.TryGetProperty("message", out var messageElement))
                {
                    var message = messageElement.GetString();
                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        extractedMessage = message;
                    }
                }
            }
            catch (JsonException)
            {
            }
        }

        if (statusCode == HttpStatusCode.Unauthorized)
        {
            return "Authentication failed. Please log out and log in again.";
        }

        if (statusCode == HttpStatusCode.Forbidden)
        {
            if (ContainsAuthenticationFailure(extractedMessage) || ContainsAuthenticationFailure(errorBody))
            {
                return "Authentication failed. Please log out and log in again.";
            }

            return "You do not have permission to perform this action.";
        }

        if (!string.IsNullOrWhiteSpace(extractedMessage))
        {
            return extractedMessage;
        }

        if (!string.IsNullOrWhiteSpace(fallbackMessage))
        {
            return fallbackMessage;
        }

        return string.IsNullOrWhiteSpace(errorBody) ? "Request failed." : errorBody;
    }

    private static bool ContainsAuthenticationFailure(string? message)
    {
        return !string.IsNullOrWhiteSpace(message) &&
               (message.Contains("not authenticated", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("unauthenticated", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("authentication failed", StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class DriverAssignedCustomerUpdateResponse
{
    public int UpdatedDriverCount { get; set; }
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
