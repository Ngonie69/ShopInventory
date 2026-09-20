using System.Net.Http.Json;
using ShopInventory.Web.Common.Http;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

/// <summary>
/// The thin transport for <c>/api/stock-write-offs</c>. URLs are copied from the controller's route
/// attributes: a string here that matches no route reads as a clean "nothing found" forever.
/// </summary>
public interface IStockWriteOffService
{
    Task<(bool Success, string Message, StockWriteOffListResponse? Value)> GetWriteOffsAsync(
        string? status, string? warehouseCode, int page, int pageSize);

    Task<(bool Success, string Message, StockWriteOffDetail? Value)> GetWriteOffAsync(int id);

    Task<(bool Success, string Message, StockWriteOffReasonsResponse? Value)> GetReasonsAsync();

    Task<(bool Success, string Message, StockWriteOffResult? Value)> CreateAsync(CreateStockWriteOffRequest request);
}

public sealed class StockWriteOffService(HttpClient httpClient, ILogger<StockWriteOffService> logger)
    : IStockWriteOffService
{
    public async Task<(bool Success, string Message, StockWriteOffListResponse? Value)> GetWriteOffsAsync(
        string? status, string? warehouseCode, int page, int pageSize)
    {
        const string fallback = "The write-offs could not be loaded.";
        try
        {
            var query = $"page={page}&pageSize={pageSize}";
            if (!string.IsNullOrWhiteSpace(status))
                query += $"&status={Uri.EscapeDataString(status)}";
            if (!string.IsNullOrWhiteSpace(warehouseCode))
                query += $"&warehouseCode={Uri.EscapeDataString(warehouseCode.Trim())}";

            return await ReadAsync<StockWriteOffListResponse>($"api/stock-write-offs?{query}", fallback);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching stock write-offs");
            return (false, ApiErrorResponse.GetFriendlyMessage(ex, fallback), null);
        }
    }

    public async Task<(bool Success, string Message, StockWriteOffDetail? Value)> GetWriteOffAsync(int id)
    {
        var fallback = $"Write-off {id} could not be loaded.";
        try
        {
            return await ReadAsync<StockWriteOffDetail>($"api/stock-write-offs/{id}", fallback);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching stock write-off {Id}", id);
            return (false, ApiErrorResponse.GetFriendlyMessage(ex, fallback), null);
        }
    }

    public async Task<(bool Success, string Message, StockWriteOffReasonsResponse? Value)> GetReasonsAsync()
    {
        const string fallback = "The write-off reasons could not be loaded.";
        try
        {
            return await ReadAsync<StockWriteOffReasonsResponse>("api/stock-write-offs/reasons", fallback);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching stock write-off reasons");
            return (false, ApiErrorResponse.GetFriendlyMessage(ex, fallback), null);
        }
    }

    public async Task<(bool Success, string Message, StockWriteOffResult? Value)> CreateAsync(
        CreateStockWriteOffRequest request)
    {
        const string fallback = "The stock could not be written off.";
        try
        {
            using var response = await httpClient.PostAsJsonAsync("api/stock-write-offs", request);
            if (!response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync();
                logger.LogWarning("POST api/stock-write-offs answered {StatusCode}", (int)response.StatusCode);
                return (false, ApiErrorResponse.GetFriendlyMessage(
                    response.StatusCode, text, fallback, forbiddenMessage: ProblemDetailReader.ReadMessage(text)), null);
            }

            var result = await response.Content.ReadFromJsonAsync<StockWriteOffResult>();
            return result is null ? (false, fallback, null) : (true, result.Message, result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error posting a stock write-off");
            return (false, ApiErrorResponse.GetFriendlyMessage(ex, fallback), null);
        }
    }

    private async Task<(bool Success, string Message, T? Value)> ReadAsync<T>(string path, string fallback)
        where T : class
    {
        using var response = await httpClient.GetAsync(path);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            logger.LogWarning("GET {Path} answered {StatusCode}", path, (int)response.StatusCode);
            return (false, ApiErrorResponse.GetFriendlyMessage(
                response.StatusCode, body, fallback, forbiddenMessage: ProblemDetailReader.ReadMessage(body)), null);
        }

        var value = await response.Content.ReadFromJsonAsync<T>();
        return value is null ? (false, fallback, null) : (true, string.Empty, value);
    }
}
