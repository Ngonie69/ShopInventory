using Microsoft.Extensions.Caching.Memory;

namespace ShopInventory.Services.Fiscalisation;

public interface IFiscalDeviceConfigCache
{
    /// <summary>
    /// Device configuration, cached. Returns null when the platform could not be reached — callers
    /// must degrade rather than fail, because this is only ever needed to decorate a result that has
    /// already succeeded.
    /// </summary>
    Task<FiscalConfigApiResponse?> TryGetAsync(int deviceId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Caches <c>GET /api/fiscal-config</c> per device.
/// </summary>
/// <remarks>
/// Needed because the QR payload cannot be composed without the device's QR URL, and the device serial
/// number is not on the submit response either. Both are stable for the life of a device certificate,
/// so fetching them on every fiscalisation would be a wasted round trip on the critical path.
///
/// A lookup failure returns null rather than throwing. By the time a caller needs this, the receipt is
/// already fiscalised at FDMS — failing here would turn a successful, irreversible submission into a
/// reported error, and could trigger a resubmit. A missing QR is repairable later by the backfill; a
/// duplicate fiscal receipt is not repairable at all.
/// </remarks>
public class FiscalDeviceConfigCache : IFiscalDeviceConfigCache
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(15);
    private const string CacheKeyPrefix = "fiscal-device-config:";

    private readonly IFiscalisationApiClient _client;
    private readonly IMemoryCache _memoryCache;
    private readonly ILogger<FiscalDeviceConfigCache> _logger;

    public FiscalDeviceConfigCache(
        IFiscalisationApiClient client,
        IMemoryCache memoryCache,
        ILogger<FiscalDeviceConfigCache> logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _memoryCache = memoryCache ?? throw new ArgumentNullException(nameof(memoryCache));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<FiscalConfigApiResponse?> TryGetAsync(
        int deviceId,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = CacheKeyPrefix + deviceId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (_memoryCache.TryGetValue(cacheKey, out FiscalConfigApiResponse? cached) && cached is not null)
        {
            return cached;
        }

        try
        {
            var config = await _client.GetFiscalConfigAsync(deviceId, cancellationToken);
            _memoryCache.Set(cacheKey, config, CacheDuration);
            return config;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Could not read fiscal configuration for device {DeviceId}. "
                + "Receipt QR codes will be left unset until the next successful lookup.",
                deviceId);

            return null;
        }
    }
}
