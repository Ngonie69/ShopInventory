using System.Net;
using System.Net.Http.Json;
using ShopInventory.Web.Common.Http;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

/// <summary>Transport for <c>/api/count-variance</c>. No decisions live here; the feature handlers make them.</summary>
public interface ICountVarianceService
{
    Task<(bool Success, string Message, CountingDocumentListResponse? Value)> GetDocumentsAsync(
        string? status, string? search, CancellationToken cancellationToken);

    Task<(bool Success, string Message, CountVarianceReport? Value)> GetReportAsync(
        int documentEntry, CancellationToken cancellationToken);

    Task<(bool Success, string Message, VanCountVarianceReport? Value)> GetVanReportAsync(
        DateTime fromDate, DateTime toDate, CancellationToken cancellationToken);
}

public sealed class CountVarianceService(HttpClient httpClient, ILogger<CountVarianceService> logger)
    : ICountVarianceService
{
    public Task<(bool Success, string Message, CountingDocumentListResponse? Value)> GetDocumentsAsync(
        string? status, string? search, CancellationToken cancellationToken)
    {
        var query = $"status={Uri.EscapeDataString(status ?? "open")}";
        if (!string.IsNullOrWhiteSpace(search))
            query += $"&search={Uri.EscapeDataString(search.Trim())}";

        return ReadAsync<CountingDocumentListResponse>(
            $"api/count-variance/documents?{query}", "The inventory counts could not be loaded.", cancellationToken);
    }

    public Task<(bool Success, string Message, CountVarianceReport? Value)> GetReportAsync(
        int documentEntry, CancellationToken cancellationToken)
        => ReadAsync<CountVarianceReport>(
            $"api/count-variance/{documentEntry}", "The count variance could not be loaded.", cancellationToken);

    public Task<(bool Success, string Message, VanCountVarianceReport? Value)> GetVanReportAsync(
        DateTime fromDate, DateTime toDate, CancellationToken cancellationToken)
        => ReadAsync<VanCountVarianceReport>(
            $"api/count-variance/vans?fromDate={fromDate:yyyy-MM-dd}&toDate={toDate:yyyy-MM-dd}",
            "The van counts could not be loaded.", cancellationToken);

    private async Task<(bool Success, string Message, T? Value)> ReadAsync<T>(
        string path, string fallback, CancellationToken cancellationToken)
        where T : class
    {
        using var response = await httpClient.GetAsync(path, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogWarning("GET {Path} answered {StatusCode}", path, (int)response.StatusCode);

            // A SAP refusal or a missing count reaches here as a 400/404 whose title says which; the
            // generic sentence would hide it.
            var problem = ProblemDetailReader.ReadMessage(body);
            var message = response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound && problem is not null
                ? problem
                : ApiErrorResponse.GetFriendlyMessage(response.StatusCode, body, fallback, forbiddenMessage: problem);
            return (false, message, null);
        }

        var value = await response.Content.ReadFromJsonAsync<T>(cancellationToken);
        return value is null ? (false, fallback, null) : (true, string.Empty, value);
    }
}
