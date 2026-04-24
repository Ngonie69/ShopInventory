using System.Net.Http.Json;
using System.Text.Json;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

public interface IPurchaseInvoiceService
{
    Task<PurchaseInvoiceListResponse?> GetPurchaseInvoicesAsync(
        int page = 1,
        int pageSize = 20,
        string? cardCode = null,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        CancellationToken cancellationToken = default);

    Task<PurchaseInvoiceDto?> GetPurchaseInvoiceByDocEntryAsync(int docEntry, CancellationToken cancellationToken = default);
    Task<PurchaseInvoiceDto?> CreatePurchaseInvoiceAsync(CreatePurchaseInvoiceRequest request, CancellationToken cancellationToken = default);
}

public class PurchaseInvoiceService(HttpClient httpClient, ILogger<PurchaseInvoiceService> logger) : IPurchaseInvoiceService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<PurchaseInvoiceListResponse?> GetPurchaseInvoicesAsync(
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

            var url = $"api/purchaseinvoice?{string.Join("&", queryParams)}";
            var response = await httpClient.GetAsync(url, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("Failed to load purchase invoices: {StatusCode} - {Content}", response.StatusCode, content);
                return null;
            }

            return JsonSerializer.Deserialize<PurchaseInvoiceListResponse>(content, JsonOptions);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading purchase invoices");
            return null;
        }
    }

    public async Task<PurchaseInvoiceDto?> GetPurchaseInvoiceByDocEntryAsync(int docEntry, CancellationToken cancellationToken = default)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<PurchaseInvoiceDto>($"api/purchaseinvoice/{docEntry}", cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading purchase invoice {DocEntry}", docEntry);
            return null;
        }
    }

    public async Task<PurchaseInvoiceDto?> CreatePurchaseInvoiceAsync(CreatePurchaseInvoiceRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            logger.LogInformation(
                "Creating purchase invoice for supplier {CardCode} with {LineCount} lines",
                request.CardCode,
                request.Lines.Count);

            var response = await httpClient.PostAsJsonAsync("api/purchaseinvoice", request, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return JsonSerializer.Deserialize<PurchaseInvoiceDto>(content, JsonOptions);
            }

            logger.LogWarning("Failed to create purchase invoice: {StatusCode} - {Content}", response.StatusCode, content);
            throw new HttpRequestException($"API returned {(int)response.StatusCode}: {content}");
        }
        catch (HttpRequestException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating purchase invoice");
            throw;
        }
    }
}