using System.Globalization;
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

    /// <summary>One route customer's sales, orders and product mix. Throws so the page can say why.</summary>
    Task<RouteCustomerSalesDetailModel> GetRouteCustomerSalesAsync(int id, DateTime? from = null, DateTime? to = null);

    /// <summary>
    /// Every route customer's trading, grouped by route. Also the source for the dormancy view —
    /// customers who bought nothing come back too, rather than being filtered out server-side.
    /// </summary>
    Task<RouteCustomerSalesSummaryModel> GetSalesSummaryAsync(
        string? assignedBusinessPartnerCode = null,
        DateTime? from = null,
        DateTime? to = null,
        int? dormantDays = null,
        bool includeInactive = true);

    Task<RouteCustomerProductMixModel> GetProductMixAsync(
        string? assignedBusinessPartnerCode = null,
        int? routeCustomerId = null,
        DateTime? from = null,
        DateTime? to = null,
        int top = 0);
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
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<List<RouteCustomerModel>>() ?? [];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching route customers");
            return [];
        }
    }

    public Task<RouteCustomerSalesDetailModel> GetRouteCustomerSalesAsync(int id, DateTime? from = null, DateTime? to = null)
    {
        var query = BuildQuery(("from", FormatDate(from)), ("to", FormatDate(to)));
        return GetReportAsync<RouteCustomerSalesDetailModel>(
            $"api/route-customers/{id}/sales{query}",
            "Failed to load this customer's sales.");
    }

    public Task<RouteCustomerSalesSummaryModel> GetSalesSummaryAsync(
        string? assignedBusinessPartnerCode = null,
        DateTime? from = null,
        DateTime? to = null,
        int? dormantDays = null,
        bool includeInactive = true)
    {
        var query = BuildQuery(
            ("assignedBusinessPartnerCode", assignedBusinessPartnerCode?.Trim()),
            ("from", FormatDate(from)),
            ("to", FormatDate(to)),
            ("dormantDays", dormantDays?.ToString(CultureInfo.InvariantCulture)),
            ("includeInactive", includeInactive.ToString().ToLowerInvariant()));

        return GetReportAsync<RouteCustomerSalesSummaryModel>(
            $"api/route-customers/sales-summary{query}",
            "Failed to load the route sales summary.");
    }

    public Task<RouteCustomerProductMixModel> GetProductMixAsync(
        string? assignedBusinessPartnerCode = null,
        int? routeCustomerId = null,
        DateTime? from = null,
        DateTime? to = null,
        int top = 0)
    {
        var query = BuildQuery(
            ("assignedBusinessPartnerCode", assignedBusinessPartnerCode?.Trim()),
            ("routeCustomerId", routeCustomerId?.ToString(CultureInfo.InvariantCulture)),
            ("from", FormatDate(from)),
            ("to", FormatDate(to)),
            ("top", top > 0 ? top.ToString(CultureInfo.InvariantCulture) : null));

        return GetReportAsync<RouteCustomerProductMixModel>(
            $"api/route-customers/product-mix{query}",
            "Failed to load the product mix.");
    }

    /// <summary>
    /// Reads a report endpoint, and throws rather than returning an empty shape when it fails.
    ///
    /// The list read above swallows its errors and answers with an empty list, which is right for a list
    /// that has a legitimate empty state. It is wrong here: a report that fails to load and one that
    /// found no sales look identical on the page, and "this customer bought nothing" is a claim the page
    /// must not make on the strength of a 500.
    /// </summary>
    private async Task<T> GetReportAsync<T>(string url, string failureMessage)
    {
        try
        {
            using var response = await SendAuthenticatedAsync(() => httpClient.GetAsync(url));

            if (!response.IsSuccessStatusCode)
            {
                var message = await ExtractErrorMessageAsync(response, failureMessage);
                logger.LogWarning("GET {Url} failed: {StatusCode} - {Message}", url, response.StatusCode, message);
                throw new InvalidOperationException(message);
            }

            return await response.Content.ReadFromJsonAsync<T>()
                ?? throw new InvalidOperationException(failureMessage);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error calling {Url}", url);
            throw new InvalidOperationException(failureMessage, ex);
        }
    }

    /// <summary>Joins the parameters that were actually supplied; an absent one is left to the API's default.</summary>
    private static string BuildQuery(params (string Key, string? Value)[] parameters)
    {
        var supplied = parameters
            .Where(parameter => !string.IsNullOrWhiteSpace(parameter.Value))
            .Select(parameter => $"{parameter.Key}={Uri.EscapeDataString(parameter.Value!)}")
            .ToList();

        return supplied.Count == 0 ? string.Empty : $"?{string.Join("&", supplied)}";
    }

    /// <summary>
    /// Dates travel as a bare <c>yyyy-MM-dd</c>. The window is whole trading days in the van's own clock,
    /// so sending an instant would hand the API a time zone it then has to undo.
    /// </summary>
    private static string? FormatDate(DateTime? value) =>
        value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

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

    private Task<HttpResponseMessage> SendAuthenticatedAsync(Func<Task<HttpResponseMessage>> sendAsync)
        => ApiTokenAuthentication.SendAsync(httpClient, authStateProvider, localStorage, sendAsync, logger);

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