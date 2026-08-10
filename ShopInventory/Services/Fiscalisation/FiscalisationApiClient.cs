using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;

namespace ShopInventory.Services.Fiscalisation;

/// <summary>
/// Typed HTTP client for the Fiscalisation platform.
/// </summary>
/// <remarks>
/// Ported from the platform's own SAP bridge client
/// (integrations/SapFiscalisationBridge/src/SapFiscalisationBridge/Clients/FiscalisationApiClient.cs)
/// so the retry semantics match the ones the platform was built to expect.
/// </remarks>
public class FiscalisationApiClient : IFiscalisationApiClient
{
    private static readonly JsonSerializerOptions ApiJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly FiscalisationSettings _settings;
    private readonly ILogger<FiscalisationApiClient> _logger;

    public FiscalisationApiClient(
        HttpClient httpClient,
        IOptions<FiscalisationSettings> settings,
        ILogger<FiscalisationApiClient> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _settings = settings?.Value ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<SubmitReceiptApiResponse> SubmitSapReceiptAsync(
        SapFiscaliseReceiptApiRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return PostWithTransientRetryAsync<SapFiscaliseReceiptApiRequest, SubmitReceiptApiResponse>(
            "api/sap/receipts/fiscalise",
            request,
            cancellationToken);
    }

    public Task<SubmitReceiptApiResponse> SubmitReceiptAsync(
        SubmitReceiptApiRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return PostWithTransientRetryAsync<SubmitReceiptApiRequest, SubmitReceiptApiResponse>(
            "api/receipts/submit",
            request,
            cancellationToken);
    }

    public async Task<CheckFiscalisedReceiptApiResponse> CheckReceiptAsync(
        int deviceId,
        string invoiceNo,
        ReceiptType receiptType,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(invoiceNo))
        {
            throw new ArgumentException("Invoice number is required", nameof(invoiceNo));
        }

        var encodedInvoiceNo = Uri.EscapeDataString(invoiceNo);
        using var response = await _httpClient.GetAsync(
            $"api/receipts/check?deviceId={deviceId}&invoiceNo={encodedInvoiceNo}&receiptType={receiptType}",
            cancellationToken);

        return await ReadResponseAsync<CheckFiscalisedReceiptApiResponse>(response, cancellationToken);
    }

    public async Task<FiscalConfigApiResponse> GetFiscalConfigAsync(
        int deviceId,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(
            $"api/fiscal-config?deviceId={deviceId}",
            cancellationToken);

        return await ReadResponseAsync<FiscalConfigApiResponse>(response, cancellationToken);
    }

    public async Task<FiscalStatusApiResponse> GetFiscalStatusAsync(
        int deviceId,
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(
            $"api/fiscal-status?deviceId={deviceId}",
            cancellationToken);

        return await ReadResponseAsync<FiscalStatusApiResponse>(response, cancellationToken);
    }

    private async Task<TResponse> PostWithTransientRetryAsync<TRequest, TResponse>(
        string requestUri,
        TRequest request,
        CancellationToken cancellationToken)
    {
        for (var retry = 0; ; retry++)
        {
            try
            {
                using var response = await _httpClient.PostAsJsonAsync(
                    requestUri, request, ApiJsonOptions, cancellationToken);

                return await ReadResponseAsync<TResponse>(response, cancellationToken);
            }
            catch (FiscalisationApiException ex) when (
                retry < _settings.TransientRetryCount && IsSafeToRetry(ex))
            {
                var delay = ResolveRetryDelay(retry, _settings.TransientRetryBaseDelayMilliseconds);
                _logger.LogWarning(
                    "Fiscalisation API rejected {RequestUri} with retryable HTTP {StatusCode}/{ErrorCode}. " +
                    "Retry {RetryNumber} of {RetryCount} in {DelayMs} ms.",
                    requestUri,
                    (int)ex.StatusCode,
                    ex.ErrorCode,
                    retry + 1,
                    _settings.TransientRetryCount,
                    delay.TotalMilliseconds);

                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Whether a failure provably happened before FDMS saw the request.
    /// </summary>
    /// <remarks>
    /// Deliberately narrow. Retrying anything else risks a second fiscal receipt for one document, and
    /// a fiscal receipt cannot be withdrawn. A timeout that may have reached FDMS is not in this list —
    /// that case is reconciled with a receipt check instead.
    ///
    /// "DeviceLockTimeout" is retained for mixed-version deployments: the console stopped emitting it,
    /// but we may be pointed at an older one.
    /// </remarks>
    internal static bool IsSafeToRetry(FiscalisationApiException exception) =>
        (exception.StatusCode == HttpStatusCode.TooManyRequests
            && exception.ErrorCode is "DeviceLockTimeout"
                or "FdmsPreflightTimeout"
                or "FdmsTimeout"
                or "TooManyConcurrentRequests")
        || (exception.StatusCode == HttpStatusCode.ServiceUnavailable
            && exception.ErrorCode is "FdmsRequestNotSent");

    internal static TimeSpan ResolveRetryDelay(int retry, int baseDelayMilliseconds)
    {
        var exponent = Math.Min(retry, 4);
        var backoff = Math.Min(5_000, Math.Max(100, baseDelayMilliseconds) * (1 << exponent));
        return TimeSpan.FromMilliseconds(backoff + Random.Shared.Next(0, 151));
    }

    private async Task<T> ReadResponseAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            var value = await response.Content.ReadFromJsonAsync<T>(ApiJsonOptions, cancellationToken);
            if (value is null)
            {
                throw new FiscalisationApiException(
                    response.StatusCode,
                    "EmptyResponse",
                    $"Fiscalisation API returned an empty {typeof(T).Name} response.");
            }

            return value;
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        ErrorApiResponse? error = null;

        try
        {
            error = JsonSerializer.Deserialize<ErrorApiResponse>(responseBody, ApiJsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Fiscalisation API returned non-standard error payload: {ResponseBody}", responseBody);
        }

        var detail = !string.IsNullOrWhiteSpace(error?.Detail)
            ? error.Detail
            : string.IsNullOrWhiteSpace(responseBody)
                ? response.ReasonPhrase ?? "Fiscalisation API request failed."
                : responseBody;

        throw new FiscalisationApiException(response.StatusCode, error?.ErrorCode, detail);
    }
}
