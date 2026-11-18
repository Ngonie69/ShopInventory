using System.Globalization;
using System.Net.Http.Json;
using ShopInventory.Web.Common.Http;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

/// <summary>
/// The thin transport for the stock ledger's two reads: a warehouse's stock at a moment, and the
/// movements between two moments. URLs are copied from StockController's route attributes.
/// </summary>
public interface IStockHistoryService
{
    Task<(bool Success, string Message, StockAsAtResponse? Value)> GetStockAsAtAsync(
        string warehouseCode, DateTime atUtc);

    Task<(bool Success, string Message, StockMovementsResponse? Value)> GetMovementsAsync(
        string warehouseCode, DateTime fromUtc, DateTime toUtc, string? itemCode = null);

    Task<(bool Success, string Message, StockMovementTotalsResponse? Value)> GetMovementTotalsAsync(
        string warehouseCode, DateTime fromUtc, DateTime toUtc);
}

public sealed class StockHistoryService(HttpClient httpClient, ILogger<StockHistoryService> logger)
    : IStockHistoryService
{
    public async Task<(bool Success, string Message, StockAsAtResponse? Value)> GetStockAsAtAsync(
        string warehouseCode, DateTime atUtc)
    {
        const string fallback = "The stock at that moment could not be read.";
        try
        {
            var path = $"api/Stock/warehouse/{Uri.EscapeDataString(warehouseCode)}/as-at?at={Stamp(atUtc)}";
            return await ReadAsync<StockAsAtResponse>(path, fallback);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error reading stock as at {At} for {Warehouse}", atUtc, warehouseCode);
            return (false, ApiErrorResponse.GetFriendlyMessage(ex, fallback), null);
        }
    }

    public async Task<(bool Success, string Message, StockMovementsResponse? Value)> GetMovementsAsync(
        string warehouseCode, DateTime fromUtc, DateTime toUtc, string? itemCode = null)
    {
        const string fallback = "The stock movements could not be read.";
        try
        {
            var path = $"api/Stock/warehouse/{Uri.EscapeDataString(warehouseCode)}/movements" +
                       $"?fromUtc={Stamp(fromUtc)}&toUtc={Stamp(toUtc)}";
            if (!string.IsNullOrWhiteSpace(itemCode))
                path += $"&itemCode={Uri.EscapeDataString(itemCode.Trim())}";

            return await ReadAsync<StockMovementsResponse>(path, fallback);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error reading stock movements for {Warehouse}", warehouseCode);
            return (false, ApiErrorResponse.GetFriendlyMessage(ex, fallback), null);
        }
    }

    public async Task<(bool Success, string Message, StockMovementTotalsResponse? Value)> GetMovementTotalsAsync(
        string warehouseCode, DateTime fromUtc, DateTime toUtc)
    {
        const string fallback = "The stock movement totals could not be read.";
        try
        {
            var path = $"api/Stock/warehouse/{Uri.EscapeDataString(warehouseCode)}/movement-totals" +
                       $"?fromUtc={Stamp(fromUtc)}&toUtc={Stamp(toUtc)}";
            return await ReadAsync<StockMovementTotalsResponse>(path, fallback);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error reading stock movement totals for {Warehouse}", warehouseCode);
            return (false, ApiErrorResponse.GetFriendlyMessage(ex, fallback), null);
        }
    }

    /// <summary>An unambiguous UTC instant for the query string.</summary>
    private static string Stamp(DateTime utc) => Uri.EscapeDataString(
        DateTime.SpecifyKind(utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : utc, DateTimeKind.Utc)
            .ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));

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
