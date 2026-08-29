using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Blazored.LocalStorage;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

/// <summary>Van sales customers' ordering-app sign-ins, as the back office administers them.</summary>
public interface IVanSalesCustomerAccountService
{
    /// <summary>The sign-ins on file, optionally narrowed to one shop.</summary>
    Task<List<VanSalesCustomerAccountModel>> GetAccountsAsync(
        int? routeCustomerId,
        bool includeInactive,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Give a shop a sign-in, or re-point an existing one at a new handset. Throws with the API's
    /// reason so the page can show it.
    /// </summary>
    Task<VanSalesCustomerAccountModel> OnboardAsync(
        OnboardVanSalesCustomerAccountModel request,
        CancellationToken cancellationToken = default);

    /// <summary>Withdraw a sign-in and end the sessions it holds.</summary>
    Task<VanSalesCustomerAccountModel> DeactivateAsync(
        int accountId,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
/// <remarks>
/// Follows <see cref="VanSalesOrderService"/>: reads return an empty result on failure so the screen
/// still renders, writes throw. The asymmetry matters more here than there — an operator who presses
/// "Give access" and sees nothing happen will press it again, and on this endpoint the second press
/// against a number already taken by another shop is refused rather than silently ignored.
/// </remarks>
public class VanSalesCustomerAccountService(
    HttpClient httpClient,
    ILogger<VanSalesCustomerAccountService> logger,
    ILocalStorageService localStorage,
    CustomAuthStateProvider authStateProvider
) : IVanSalesCustomerAccountService
{
    private const string BaseUrl = "api/van-sales-customer-accounts";

    public async Task<List<VanSalesCustomerAccountModel>> GetAccountsAsync(
        int? routeCustomerId,
        bool includeInactive,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var query = BuildQuery(
                ("routeCustomerId", routeCustomerId?.ToString(CultureInfo.InvariantCulture)),
                ("includeInactive", includeInactive ? "true" : null));

            using var response = await SendAuthenticatedAsync(
                () => httpClient.GetAsync($"{BaseUrl}{query}", cancellationToken));

            response.EnsureSuccessStatusCode();

            return await response.Content.ReadFromJsonAsync<List<VanSalesCustomerAccountModel>>(cancellationToken)
                   ?? new List<VanSalesCustomerAccountModel>();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading van sales customer sign-ins");
            return new List<VanSalesCustomerAccountModel>();
        }
    }

    public async Task<VanSalesCustomerAccountModel> OnboardAsync(
        OnboardVanSalesCustomerAccountModel request,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthenticatedAsync(
            () => httpClient.PostAsJsonAsync(BaseUrl, request, cancellationToken));

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                await ExtractErrorMessageAsync(response, "The sign-in could not be set up."));
        }

        return await response.Content.ReadFromJsonAsync<VanSalesCustomerAccountModel>(cancellationToken)
               ?? throw new InvalidOperationException("The sign-in could not be set up.");
    }

    public async Task<VanSalesCustomerAccountModel> DeactivateAsync(
        int accountId,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthenticatedAsync(
            () => httpClient.PostAsync($"{BaseUrl}/{accountId}/deactivate", content: null, cancellationToken));

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                await ExtractErrorMessageAsync(response, "The sign-in could not be withdrawn."));
        }

        return await response.Content.ReadFromJsonAsync<VanSalesCustomerAccountModel>(cancellationToken)
               ?? throw new InvalidOperationException("The sign-in could not be withdrawn.");
    }

    private Task<HttpResponseMessage> SendAuthenticatedAsync(Func<Task<HttpResponseMessage>> sendAsync)
        => ApiTokenAuthentication.SendAsync(httpClient, authStateProvider, localStorage, sendAsync, logger);

    private static string BuildQuery(params (string Name, string? Value)[] parameters)
    {
        var pairs = parameters
            .Where(p => !string.IsNullOrWhiteSpace(p.Value))
            .Select(p => $"{p.Name}={Uri.EscapeDataString(p.Value!)}")
            .ToList();

        return pairs.Count == 0 ? string.Empty : "?" + string.Join("&", pairs);
    }

    /// <summary>
    /// The API's own explanation, or a fallback.
    /// </summary>
    /// <remarks>
    /// Reads <c>detail</c> from the problem details the API returns, which is where the sentence the
    /// operator needs lives — "that number is already signed in as another customer" is actionable
    /// in a way that "400 Bad Request" is not.
    /// </remarks>
    private static async Task<string> ExtractErrorMessageAsync(HttpResponseMessage response, string fallback)
    {
        var content = await response.Content.ReadAsStringAsync();

        if (string.IsNullOrWhiteSpace(content))
        {
            return fallback;
        }

        try
        {
            using var document = JsonDocument.Parse(content);

            if (document.RootElement.TryGetProperty("detail", out var detail)
                && detail.ValueKind == JsonValueKind.String)
            {
                var message = detail.GetString();
                if (!string.IsNullOrWhiteSpace(message))
                {
                    return message;
                }
            }
        }
        catch (JsonException)
        {
            // Not problem details — a proxy page, most likely. The fallback still says something.
        }

        return fallback;
    }
}
