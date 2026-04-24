using System.Net.Http.Json;
using System.Text.Json;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

public interface IPurchaseRequestService
{
    Task<PurchaseRequestListResponse?> GetPurchaseRequestsAsync(
        int page = 1,
        int pageSize = 20,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        CancellationToken cancellationToken = default);

    Task<PurchaseRequestDto?> GetPurchaseRequestByDocEntryAsync(int docEntry, CancellationToken cancellationToken = default);
    Task<PurchaseRequestDto?> CreatePurchaseRequestAsync(CreatePurchaseRequestRequest request, CancellationToken cancellationToken = default);
}

public class PurchaseRequestService(HttpClient httpClient, ILogger<PurchaseRequestService> logger) : IPurchaseRequestService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<PurchaseRequestListResponse?> GetPurchaseRequestsAsync(
        int page = 1,
        int pageSize = 20,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var queryParams = new List<string>
            {
                $"page={page}",
                $"pageSize={pageSize}"
            };

            if (fromDate.HasValue)
                queryParams.Add($"fromDate={fromDate.Value:yyyy-MM-dd}");
            if (toDate.HasValue)
                queryParams.Add($"toDate={toDate.Value:yyyy-MM-dd}");

            var url = $"api/purchaserequest?{string.Join("&", queryParams)}";
            var response = await httpClient.GetAsync(url, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("Failed to load purchase requests: {StatusCode} - {Content}", response.StatusCode, content);
                return null;
            }

            return JsonSerializer.Deserialize<PurchaseRequestListResponse>(content, JsonOptions);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading purchase requests");
            return null;
        }
    }

    public async Task<PurchaseRequestDto?> GetPurchaseRequestByDocEntryAsync(int docEntry, CancellationToken cancellationToken = default)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<PurchaseRequestDto>($"api/purchaserequest/{docEntry}", cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading purchase request {DocEntry}", docEntry);
            return null;
        }
    }

    public async Task<PurchaseRequestDto?> CreatePurchaseRequestAsync(CreatePurchaseRequestRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await httpClient.PostAsJsonAsync("api/purchaserequest", request, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return JsonSerializer.Deserialize<PurchaseRequestDto>(content, JsonOptions);
            }

            logger.LogWarning("Failed to create purchase request: {StatusCode} - {Content}", response.StatusCode, content);
            throw new HttpRequestException($"API returned {(int)response.StatusCode}: {content}");
        }
        catch (HttpRequestException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating purchase request");
            throw;
        }
    }
}