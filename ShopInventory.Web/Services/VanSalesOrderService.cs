using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Blazored.LocalStorage;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

/// <summary>Orders van sales customers placed for themselves, as the back office sees them.</summary>
public interface IVanSalesOrderService
{
    /// <summary>What a van has been asked to carry on a given day.</summary>
    Task<VanSalesRouteLoadModel> GetRouteLoadAsync(
        string? assignedBusinessPartnerCode,
        string? routeCode,
        DateTime? visitDate,
        VanSalesOrderStatusModel? status);

    /// <summary>Record what was actually delivered. Throws with the API's reason so the page can show it.</summary>
    Task<VanSalesOrderModel> RecordDeliveryAsync(int orderId, RecordVanSalesDeliveryModel request);

    /// <summary>Turn a customer's order into a sales order.</summary>
    Task<VanSalesOrderConversionModel> ConvertAsync(int orderId);
}

/// <inheritdoc />
/// <remarks>
/// Reads return an empty result on failure so a dashboard does not break; writes throw. The
/// asymmetry is deliberate — an operator who presses "record delivery" and sees nothing happen will
/// press it again, and the second press must not be their first indication that the first failed.
/// </remarks>
public class VanSalesOrderService(
    HttpClient httpClient,
    ILogger<VanSalesOrderService> logger,
    ILocalStorageService localStorage,
    CustomAuthStateProvider authStateProvider
) : IVanSalesOrderService
{
    public async Task<VanSalesRouteLoadModel> GetRouteLoadAsync(
        string? assignedBusinessPartnerCode,
        string? routeCode,
        DateTime? visitDate,
        VanSalesOrderStatusModel? status)
    {
        try
        {
            var query = BuildQuery(
                ("assignedBusinessPartnerCode", assignedBusinessPartnerCode?.Trim()),
                ("routeCode", routeCode?.Trim()),
                ("visitDate", visitDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                ("status", status?.ToString()));

            using var response = await SendAuthenticatedAsync(
                () => httpClient.GetAsync($"api/van-sales-orders/route-load{query}"));

            response.EnsureSuccessStatusCode();

            return await response.Content.ReadFromJsonAsync<VanSalesRouteLoadModel>() ?? new VanSalesRouteLoadModel();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading the van sales customer order load list");
            return new VanSalesRouteLoadModel();
        }
    }

    public async Task<VanSalesOrderModel> RecordDeliveryAsync(int orderId, RecordVanSalesDeliveryModel request)
    {
        using var response = await SendAuthenticatedAsync(
            () => httpClient.PostAsJsonAsync($"api/van-sales-orders/{orderId}/delivery", request));

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                await ExtractErrorMessageAsync(response, "The delivery could not be recorded."));
        }

        return await response.Content.ReadFromJsonAsync<VanSalesOrderModel>()
               ?? throw new InvalidOperationException("The delivery could not be recorded.");
    }

    public async Task<VanSalesOrderConversionModel> ConvertAsync(int orderId)
    {
        using var response = await SendAuthenticatedAsync(
            () => httpClient.PostAsync($"api/van-sales-orders/{orderId}/convert", content: null));

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                await ExtractErrorMessageAsync(response, "The order could not be converted."));
        }

        return await response.Content.ReadFromJsonAsync<VanSalesOrderConversionModel>()
               ?? throw new InvalidOperationException("The order could not be converted.");
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
    /// Reads <c>detail</c> from the problem details the API returns. That is where the useful
    /// sentence lives — "more was delivered than ordered for FRM001" — and showing the status code
    /// instead would leave the operator guessing at something the server already explained.
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
