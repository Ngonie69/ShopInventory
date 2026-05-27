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
    ILocalStorageService localStorage
) : IRouteCustomerService
{
    public async Task<List<RouteCustomerModel>> GetRouteCustomersAsync(string? assignedBusinessPartnerCode = null, bool activeOnly = true)
    {
        try
        {
            await EnsureAuthenticationAsync(httpClient, localStorage);

            var queryParams = new List<string> { $"activeOnly={activeOnly.ToString().ToLowerInvariant()}" };
            if (!string.IsNullOrWhiteSpace(assignedBusinessPartnerCode))
            {
                queryParams.Add($"assignedBusinessPartnerCode={Uri.EscapeDataString(assignedBusinessPartnerCode.Trim())}");
            }

            var url = $"api/route-customers?{string.Join("&", queryParams)}";
            return await httpClient.GetFromJsonAsync<List<RouteCustomerModel>>(url) ?? [];
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
            await EnsureAuthenticationAsync(httpClient, localStorage);

            var response = await httpClient.PutAsJsonAsync($"api/route-customers/{id}", request);
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
            await EnsureAuthenticationAsync(httpClient, localStorage);

            var response = await httpClient.DeleteAsync($"api/route-customers/{id}");
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

    private static async Task EnsureAuthenticationAsync(HttpClient httpClient, ILocalStorageService localStorage)
    {
        try
        {
            var token = await localStorage.GetItemAsync<string>("authToken");
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