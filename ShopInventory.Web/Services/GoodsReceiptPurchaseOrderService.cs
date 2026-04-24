using System.Net.Http.Json;
using System.Text.Json;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

public interface IGoodsReceiptPurchaseOrderService
{
    Task<GoodsReceiptPurchaseOrderListResponse?> GetGoodsReceiptPurchaseOrdersAsync(
        int page = 1,
        int pageSize = 20,
        string? cardCode = null,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        CancellationToken cancellationToken = default);

    Task<GoodsReceiptPurchaseOrderDto?> GetGoodsReceiptPurchaseOrderByDocEntryAsync(int docEntry, CancellationToken cancellationToken = default);
    Task<GoodsReceiptPurchaseOrderDto?> CreateGoodsReceiptPurchaseOrderAsync(CreateGoodsReceiptPurchaseOrderRequest request, CancellationToken cancellationToken = default);
}

public class GoodsReceiptPurchaseOrderService(HttpClient httpClient, ILogger<GoodsReceiptPurchaseOrderService> logger) : IGoodsReceiptPurchaseOrderService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<GoodsReceiptPurchaseOrderListResponse?> GetGoodsReceiptPurchaseOrdersAsync(
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

            var url = $"api/goodsreceiptpurchaseorder?{string.Join("&", queryParams)}";
            var response = await httpClient.GetAsync(url, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("Failed to load goods receipt POs: {StatusCode} - {Content}", response.StatusCode, content);
                return null;
            }

            return JsonSerializer.Deserialize<GoodsReceiptPurchaseOrderListResponse>(content, JsonOptions);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading goods receipt POs");
            return null;
        }
    }

    public async Task<GoodsReceiptPurchaseOrderDto?> GetGoodsReceiptPurchaseOrderByDocEntryAsync(int docEntry, CancellationToken cancellationToken = default)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<GoodsReceiptPurchaseOrderDto>($"api/goodsreceiptpurchaseorder/{docEntry}", cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error loading goods receipt PO {DocEntry}", docEntry);
            return null;
        }
    }

    public async Task<GoodsReceiptPurchaseOrderDto?> CreateGoodsReceiptPurchaseOrderAsync(CreateGoodsReceiptPurchaseOrderRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await httpClient.PostAsJsonAsync("api/goodsreceiptpurchaseorder", request, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return JsonSerializer.Deserialize<GoodsReceiptPurchaseOrderDto>(content, JsonOptions);
            }

            logger.LogWarning("Failed to create goods receipt PO: {StatusCode} - {Content}", response.StatusCode, content);
            throw new HttpRequestException($"API returned {(int)response.StatusCode}: {content}");
        }
        catch (HttpRequestException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating goods receipt PO");
            throw;
        }
    }
}