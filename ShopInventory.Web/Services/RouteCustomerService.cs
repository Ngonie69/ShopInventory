using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Blazored.LocalStorage;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

public interface IRouteCustomerService
{
    Task<List<RouteCustomerModel>> GetRouteCustomersAsync(string? assignedBusinessPartnerCode = null, bool activeOnly = true);
    Task<RouteCustomerModel> UpdateRouteCustomerAsync(int id, UpdateRouteCustomerRequest request);
    Task DeleteRouteCustomerAsync(int id);
}

public class RouteCustomerService(
    HttpClient httpClient,
    ILogger<RouteCustomerService> logger,
    ILocalStorageService localStorage,
    CustomAuthStateProvider authStateProvider
) : IRouteCustomerService
{
    public async Task<List<RouteCustomerModel>> GetRouteCustomersAsync(string? assignedBusinessPartnerCode = null, bool activeOnly = true)
    {
        try
        {
            var queryParams = new List<string> { $"activeOnly={activeOnly.ToString().ToLowerInvariant()}" };
            if (!string.IsNullOrWhiteSpace(assignedBusinessPartnerCode))
            {
                queryParams.Add($"assignedBusinessPartnerCode={Uri.EscapeDataString(assignedBusinessPartnerCode.Trim())}");
            }

            var url = $"api/route-customers?{string.Join("&", queryParams)}";
            using var response = await SendAuthenticatedAsync(() => httpClient.GetAsync(url));
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Failed to fetch route customers: {StatusCode}", (int)response.StatusCode);
                return [];
            }

            return await response.Content.ReadFromJsonAsync<List<RouteCustomerModel>>() ?? [];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching route customers");
            return [];
        }
    }

    public async Task<RouteCustomerModel> UpdateRouteCustomerAsync(int id, UpdateRouteCustomerRequest request)
    {
        try
        {
            using var response = await SendAuthenticatedAsync(() => httpClient.PutAsJsonAsync($"api/route-customers/{id}", request));
            if (!response.IsSuccessStatusCode)
            {
                var message = await ExtractErrorMessageAsync(response, "Failed to update route customer.");
                logger.LogWarning("Failed to update route customer {RouteCustomerId}: {StatusCode} - {Message}", id, response.StatusCode, message);
                throw new InvalidOperationException(message);
            }

            return await response.Content.ReadFromJsonAsync<RouteCustomerModel>()
                ?? throw new InvalidOperationException("The server returned an empty route customer response.");
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating route customer {RouteCustomerId}", id);
            throw new InvalidOperationException("Failed to update route customer.", ex);
        }
    }

    public async Task DeleteRouteCustomerAsync(int id)
    {
        try
        {
            using var response = await SendAuthenticatedAsync(() => httpClient.DeleteAsync($"api/route-customers/{id}"));
            if (!response.IsSuccessStatusCode)
            {
                var message = await ExtractErrorMessageAsync(response, "Failed to delete route customer.");
                logger.LogWarning("Failed to delete route customer {RouteCustomerId}: {StatusCode} - {Message}", id, response.StatusCode, message);
                throw new InvalidOperationException(message);
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deleting route customer {RouteCustomerId}", id);
            throw new InvalidOperationException("Failed to delete route customer.", ex);
        }
    }

    private async Task EnsureAuthenticationAsync()
    {
        try
        {
            // Goes through the auth state provider so an expired access token is renewed from the
            // refresh token instead of being sent to the API and coming back as a 401.
            var token = await authStateProvider.GetAccessTokenAsync()
                        ?? await localStorage.GetItemAsync<string>("authToken");
            var currentToken = httpClient.DefaultRequestHeaders.Authorization?.Parameter;

            if (string.IsNullOrWhiteSpace(token))
            {
                httpClient.DefaultRequestHeaders.Authorization = null;
                return;
            }

            if (!string.Equals(currentToken, token, StringComparison.Ordinal))
            {
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
        }
        catch
        {
            httpClient.DefaultRequestHeaders.Authorization = null;
        }
    }

    /// <summary>
    /// Sends an authenticated request, renewing the access token and retrying once if the API
    /// rejects it. The request factory is invoked per attempt because a request message cannot be
    /// resent.
    /// </summary>
    private async Task<HttpResponseMessage> SendAuthenticatedAsync(Func<Task<HttpResponseMessage>> sendAsync)
    {
        await EnsureAuthenticationAsync();
        var response = await sendAsync();

        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        var rejectedToken = httpClient.DefaultRequestHeaders.Authorization?.Parameter;
        var refreshedToken = await authStateProvider.RefreshAccessTokenAsync(rejectedToken);
        if (string.IsNullOrWhiteSpace(refreshedToken) ||
            string.Equals(refreshedToken, rejectedToken, StringComparison.Ordinal))
        {
            return response;
        }

        logger.LogInformation("Retrying request after refreshing an expired access token");
        response.Dispose();
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", refreshedToken);
        return await sendAsync();
    }

    private static async Task<string> ExtractErrorMessageAsync(HttpResponseMessage response, string fallbackMessage)
    {
        var content = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(content))
        {
            return fallbackMessage;
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;

            if (root.TryGetProperty("errors", out var errorsElement)
                && errorsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in errorsElement.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    var firstError = property.Value
                        .EnumerateArray()
                        .Select(element => element.ValueKind == JsonValueKind.String ? element.GetString() : null)
                        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

                    if (!string.IsNullOrWhiteSpace(firstError))
                    {
                        return firstError!;
                    }
                }
            }

            if (root.TryGetProperty("title", out var titleElement)
                && titleElement.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(titleElement.GetString()))
            {
                return titleElement.GetString()!;
            }

            if (root.TryGetProperty("detail", out var detailElement)
                && detailElement.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(detailElement.GetString()))
            {
                return detailElement.GetString()!;
            }
        }
        catch (JsonException)
        {
        }

        return content.Trim();
    }
}