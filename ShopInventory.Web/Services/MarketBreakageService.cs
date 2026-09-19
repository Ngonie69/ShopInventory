using System.Net.Http.Json;
using ShopInventory.Web.Common.Http;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

/// <summary>
/// The thin transport for <c>/api/market-breakages</c>. URLs are copied from the controller's route
/// attributes: a string here that matches no route reads as a clean "nothing found" forever.
/// </summary>
public interface IMarketBreakageService
{
    Task<(bool Success, string Message, MarketBreakageListResponseDto? Value)> GetBreakagesAsync(
        string? status, string? search, int page, int pageSize);

    Task<(bool Success, string Message, MarketBreakageDetailDto? Value)> GetBreakageAsync(int id);

    Task<(bool Success, string Message, MarketBreakageDecisionResultDto? Value)> ConfirmAsync(
        int id, ConfirmMarketBreakageRequestDto request);

    Task<(bool Success, string Message, MarketBreakageDecisionResultDto? Value)> RejectAsync(int id, string remarks);
}

public sealed class MarketBreakageService(HttpClient httpClient, ILogger<MarketBreakageService> logger)
    : IMarketBreakageService
{
    public async Task<(bool Success, string Message, MarketBreakageListResponseDto? Value)> GetBreakagesAsync(
        string? status, string? search, int page, int pageSize)
    {
        const string fallback = "The breakage reports could not be loaded.";
        try
        {
            var query = $"page={page}&pageSize={pageSize}";
            if (!string.IsNullOrWhiteSpace(status))
                query += $"&status={Uri.EscapeDataString(status)}";
            if (!string.IsNullOrWhiteSpace(search))
                query += $"&search={Uri.EscapeDataString(search.Trim())}";

            return await ReadAsync<MarketBreakageListResponseDto>($"api/market-breakages?{query}", fallback);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching market breakage reports");
            return (false, ApiErrorResponse.GetFriendlyMessage(ex, fallback), null);
        }
    }

    public async Task<(bool Success, string Message, MarketBreakageDetailDto? Value)> GetBreakageAsync(int id)
    {
        var fallback = $"Breakage report {id} could not be loaded.";
        try
        {
            return await ReadAsync<MarketBreakageDetailDto>($"api/market-breakages/{id}", fallback);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching market breakage report {Id}", id);
            return (false, ApiErrorResponse.GetFriendlyMessage(ex, fallback), null);
        }
    }

    public Task<(bool Success, string Message, MarketBreakageDecisionResultDto? Value)> ConfirmAsync(
        int id, ConfirmMarketBreakageRequestDto request)
        => PostAsync($"api/market-breakages/{id}/confirm", request,
            "The breakage could not be confirmed.", id);

    public Task<(bool Success, string Message, MarketBreakageDecisionResultDto? Value)> RejectAsync(int id, string remarks)
        => PostAsync($"api/market-breakages/{id}/reject", new RejectMarketBreakageRequestDto { Remarks = remarks },
            "The breakage could not be rejected.", id);

    private async Task<(bool Success, string Message, MarketBreakageDecisionResultDto? Value)> PostAsync<TBody>(
        string path, TBody body, string fallback, int id)
    {
        try
        {
            using var response = await httpClient.PostAsJsonAsync(path, body);
            if (!response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync();
                logger.LogWarning("POST {Path} answered {StatusCode}", path, (int)response.StatusCode);
                return (false, ApiErrorResponse.GetFriendlyMessage(
                    response.StatusCode, text, fallback, forbiddenMessage: ProblemDetailReader.ReadMessage(text)), null);
            }

            var result = await response.Content.ReadFromJsonAsync<MarketBreakageDecisionResultDto>();
            return result is null ? (false, fallback, null) : (true, result.Message, result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error posting {Path} for market breakage {Id}", path, id);
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
