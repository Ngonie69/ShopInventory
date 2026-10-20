using System.Net.Http.Json;
using ShopInventory.Web.Common.Http;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

/// <summary>
/// The thin transport for <c>/api/factory-transfers</c>. URLs are copied from the controller's route
/// attributes: a string here that matches no route reads as a clean "nothing found" forever.
/// </summary>
public interface IFactoryTransferService
{
    Task<(bool Success, string Message, FactoryTransferListResponseDto? Value)> GetTransfersAsync(
        string? view, string? search, int page, int pageSize);

    Task<(bool Success, string Message, FactoryTransferDetailDto? Value)> GetTransferAsync(int id);

    Task<(bool Success, string Message, FactoryTransferOptionsDto? Value)> GetOptionsAsync();

    Task<(bool Success, string Message, FactoryTransferActionResultDto? Value)> SendAsync(SendFactoryTransferRequestDto request);

    Task<(bool Success, string Message, FactoryTransferActionResultDto? Value)> DecideAsync(int id, DecideFactoryTransferRequestDto request);

    Task<(bool Success, string Message, FactoryTransferActionResultDto? Value)> RecallAsync(int id, string reason);

    Task<(bool Success, string Message, FactoryTransferActionResultDto? Value)> RetryAsync(int id);

    Task<(bool Success, string Message, FactoryTransferActionResultDto? Value)> CancelAsync(int id);
}

public sealed class FactoryTransferService(HttpClient httpClient, ILogger<FactoryTransferService> logger)
    : IFactoryTransferService
{
    private const string Root = "api/factory-transfers";

    public async Task<(bool Success, string Message, FactoryTransferListResponseDto? Value)> GetTransfersAsync(
        string? view, string? search, int page, int pageSize)
    {
        const string fallback = "The factory transfers could not be loaded.";
        try
        {
            var query = $"page={page}&pageSize={pageSize}";
            if (!string.IsNullOrWhiteSpace(view))
                query += $"&view={Uri.EscapeDataString(view)}";
            if (!string.IsNullOrWhiteSpace(search))
                query += $"&search={Uri.EscapeDataString(search.Trim())}";

            return await ReadAsync<FactoryTransferListResponseDto>($"{Root}?{query}", fallback);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching factory transfers");
            return (false, ApiErrorResponse.GetFriendlyMessage(ex, fallback), null);
        }
    }

    public async Task<(bool Success, string Message, FactoryTransferDetailDto? Value)> GetTransferAsync(int id)
    {
        var fallback = $"Factory transfer {id} could not be loaded.";
        try
        {
            return await ReadAsync<FactoryTransferDetailDto>($"{Root}/{id}", fallback);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching factory transfer {Id}", id);
            return (false, ApiErrorResponse.GetFriendlyMessage(ex, fallback), null);
        }
    }

    public async Task<(bool Success, string Message, FactoryTransferOptionsDto? Value)> GetOptionsAsync()
    {
        const string fallback = "Your factory warehouses could not be loaded.";
        try
        {
            return await ReadAsync<FactoryTransferOptionsDto>($"{Root}/options", fallback);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching factory transfer options");
            return (false, ApiErrorResponse.GetFriendlyMessage(ex, fallback), null);
        }
    }

    public Task<(bool Success, string Message, FactoryTransferActionResultDto? Value)> SendAsync(SendFactoryTransferRequestDto request)
        => PostAsync(Root, request, "The transfer could not be sent.");

    public Task<(bool Success, string Message, FactoryTransferActionResultDto? Value)> DecideAsync(int id, DecideFactoryTransferRequestDto request)
        => PostAsync($"{Root}/{id}/decision", request, "Your decision could not be saved.");

    public Task<(bool Success, string Message, FactoryTransferActionResultDto? Value)> RecallAsync(int id, string reason)
        => PostAsync($"{Root}/{id}/recall", new RecallFactoryTransferRequestDto { Reason = reason }, "The transfer could not be recalled.");

    public Task<(bool Success, string Message, FactoryTransferActionResultDto? Value)> RetryAsync(int id)
        => PostAsync<object?>($"{Root}/{id}/retry", null, "The transfer could not be retried.");

    public Task<(bool Success, string Message, FactoryTransferActionResultDto? Value)> CancelAsync(int id)
        => PostAsync<object?>($"{Root}/{id}/cancel", null, "The transfer could not be cancelled.");

    private async Task<(bool Success, string Message, FactoryTransferActionResultDto? Value)> PostAsync<TBody>(
        string path, TBody body, string fallback)
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

            var result = await response.Content.ReadFromJsonAsync<FactoryTransferActionResultDto>();
            return result is null ? (false, fallback, null) : (true, result.Message, result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error posting {Path}", path);
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
