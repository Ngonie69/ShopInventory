using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.DTOs;

namespace ShopInventory.Services;

/// <summary>
/// Typed HTTP client for TransferEventListener.
/// </summary>
/// <remarks>
/// Every route here is on the listener's <c>api/Transfer</c> controller. The paths are relative and
/// carry no leading slash so the configured base URL's own path, if it ever gains one, is preserved —
/// <see cref="HttpClient.BaseAddress"/> is registered with a trailing slash for the same reason.
/// </remarks>
public sealed class TransferEventListenerClient(
    HttpClient httpClient,
    IOptions<TransferEventListenerSettings> settings,
    ILogger<TransferEventListenerClient> logger) : ITransferEventListenerClient
{
    private readonly TransferEventListenerSettings _settings = settings.Value;

    /// <summary>
    /// The listener answers camelCase, which is the web default; case-insensitivity is set anyway so
    /// a future serializer change on its side cannot quietly null every property here.
    /// </summary>
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public bool IsEnabled => _settings.Enabled;

    public string BaseUrl => _settings.BaseUrl;

    public async Task<TransferListenerHealthDto> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        EnsureEnabled();

        using var response = await httpClient.GetAsync("api/Transfer/health", cancellationToken);

        // 503 is the listener reporting itself unhealthy, and the body says why. Treating it as a
        // transport failure would throw away the one thing worth reading.
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.ServiceUnavailable)
        {
            throw new HttpRequestException(
                $"TransferEventListener health returned {(int)response.StatusCode} {response.ReasonPhrase}.",
                null,
                response.StatusCode);
        }

        var health = await response.Content.ReadFromJsonAsync<TransferListenerHealthDto>(
            SerializerOptions, cancellationToken);

        return health ?? throw new HttpRequestException(
            "TransferEventListener health returned an empty body.");
    }

    public async Task<TransferListenerWarehouseStockDto?> GetWarehouseNonBatchStockAsync(
        string warehouseCode,
        CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        ArgumentException.ThrowIfNullOrWhiteSpace(warehouseCode);

        var path = $"api/Transfer/item-quantities/{Uri.EscapeDataString(warehouseCode.Trim())}";
        using var response = await httpClient.GetAsync(path, cancellationToken);

        // The listener returns 503 when it could not read SAP, specifically so that "nothing could be
        // established" is never expressed as an empty item list. Null carries that same distinction
        // to the caller; anything else would let a failed read look like an empty warehouse.
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            logger.LogWarning(
                "TransferEventListener could not read stock for warehouse {Warehouse} from SAP",
                warehouseCode);
            return null;
        }

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<TransferListenerWarehouseStockDto>(
            SerializerOptions, cancellationToken);
    }

    public async Task<TransferListenerStatsDto> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        EnsureEnabled();

        var stats = await httpClient.GetFromJsonAsync<TransferListenerStatsDto>(
            "api/Transfer/stats", SerializerOptions, cancellationToken);

        return stats ?? new TransferListenerStatsDto();
    }

    public async Task<IReadOnlyList<string>> GetMonitoredWarehousesAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureEnabled();

        var payload = await httpClient.GetFromJsonAsync<MonitoredWarehousesResponse>(
            "api/Transfer/warehouses", SerializerOptions, cancellationToken);

        return payload?.Warehouses ?? [];
    }

    public async Task<TransferListenerCheckResultDto> TriggerCheckAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureEnabled();

        using var response = await httpClient.PostAsync("api/Transfer/check-now", null, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<TransferListenerCheckResultDto>(
            SerializerOptions, cancellationToken);

        return result ?? throw new HttpRequestException(
            "TransferEventListener check-now returned an empty body.");
    }

    /// <remarks>
    /// Thrown rather than returned empty so that a disabled integration cannot be mistaken for a
    /// listener that answered and had nothing to say. Callers gate on <see cref="IsEnabled"/>.
    /// </remarks>
    private void EnsureEnabled()
    {
        if (!_settings.Enabled)
        {
            throw new InvalidOperationException(
                "The TransferEventListener integration is disabled (TransferEventListener:Enabled).");
        }
    }

    /// <summary>
    /// The listener wraps its warehouse list in an object rather than returning a bare array.
    /// </summary>
    private sealed class MonitoredWarehousesResponse
    {
        public List<string> Warehouses { get; set; } = [];
    }
}
