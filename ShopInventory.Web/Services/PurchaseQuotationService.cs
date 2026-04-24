using System.Net.Http.Json;
using System.Text.Json;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

public interface IPurchaseQuotationService
{
    Task<PurchaseQuotationListResponse?> GetPurchaseQuotationsAsync(
        int page = 1,
        int pageSize = 20,
        string? cardCode = null,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        CancellationToken cancellationToken = default);

    Task<PurchaseQuotationDto?> GetPurchaseQuotationByDocEntryAsync(int docEntry, CancellationToken cancellationToken = default);
    Task<PurchaseQuotationDto?> CreatePurchaseQuotationAsync(CreatePurchaseQuotationRequest request, CancellationToken cancellationToken = default);
}

public class PurchaseQuotationService(HttpClient httpClient, ILogger<PurchaseQuotationService> logger) : IPurchaseQuotationService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<PurchaseQuotationListResponse?> GetPurchaseQuotationsAsync(
        int page = 1,
        int pageSize = 20,
        string? cardCode = null,
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

            if (!string.IsNullOrWhiteSpace(cardCode))
                queryParams.Add($"cardCode={Uri.EscapeDataString(cardCode)}");
            if (fromDate.HasValue)
                queryParams.Add($"fromDate={fromDate.Value:yyyy-MM-dd}");
            if (toDate.HasValue)
                queryParams.Add($"toDate={toDate.Value:yyyy-MM-dd}");

            var url = $"api/purchasequotation?{string.Join("&", queryParams)}";
            var response = await httpClient.GetAsync(url, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("Failed to load purchase quotations: {StatusCode} - {Content}", response.StatusCode, content);
                return null;
            }

            return JsonSerializer.Deserialize<PurchaseQuotationListResponse>(content, JsonOptions);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading purchase quotations");
            return null;
        }
    }

    public async Task<PurchaseQuotationDto?> GetPurchaseQuotationByDocEntryAsync(int docEntry, CancellationToken cancellationToken = default)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<PurchaseQuotationDto>($"api/purchasequotation/{docEntry}", cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading purchase quotation {DocEntry}", docEntry);
            return null;
        }
    }

    public async Task<PurchaseQuotationDto?> CreatePurchaseQuotationAsync(CreatePurchaseQuotationRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await httpClient.PostAsJsonAsync("api/purchasequotation", request, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return JsonSerializer.Deserialize<PurchaseQuotationDto>(content, JsonOptions);
            }

            logger.LogWarning("Failed to create purchase quotation: {StatusCode} - {Content}", response.StatusCode, content);
            throw new HttpRequestException($"API returned {(int)response.StatusCode}: {content}");
        }
        catch (HttpRequestException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating purchase quotation");
            throw;
        }
    }
}