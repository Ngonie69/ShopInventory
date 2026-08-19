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
    /// <summary>
    /// The error code a call fails with when no API key is configured. Raised here, before anything is
    /// sent: the platform refuses every keyless request with a 401, so the round trip only ever bought
    /// the same answer more slowly, plus a warning and a stack trace per call — 133 of them in one
    /// production day, each one restating what startup had already said once.
    /// </summary>
    public const string ApiKeyNotConfiguredErrorCode = "ApiKeyNotConfigured";

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
        ThrowIfNoApiKey();

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
        ThrowIfNoApiKey();

        return PostWithTransientRetryAsync<SubmitReceiptApiRequest, SubmitReceiptApiResponse>(
            "api/receipts/submit",
            request,
            cancellationToken);
    }

    public Task<SubmitReceiptApiResponse> IngestSignedReceiptAsync(
        IngestSignedReceiptApiRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfNoApiKey();

        return PostWithTransientRetryAsync<IngestSignedReceiptApiRequest, SubmitReceiptApiResponse>(
            "api/receipts/ingest-signed",
            request,
            cancellationToken);
    }

    public Task<PreflightReceiptApiResponse> PreflightReceiptAsync(
        SubmitReceiptApiRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfNoApiKey();

        return PostOnceAsync<SubmitReceiptApiRequest, PreflightReceiptApiResponse>(
            "api/receipts/preflight",
            request,
            cancellationToken);
    }

    public Task<PreflightReceiptApiResponse> PreflightSignedReceiptAsync(
        IngestSignedReceiptApiRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfNoApiKey();

        return PostOnceAsync<IngestSignedReceiptApiRequest, PreflightReceiptApiResponse>(
            "api/receipts/preflight-offline",
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

        ThrowIfNoApiKey();

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
        ThrowIfNoApiKey();

        using var response = await _httpClient.GetAsync(
            "api/fiscal-config" + DeviceQuery(deviceId),
            cancellationToken);

        return await ReadResponseAsync<FiscalConfigApiResponse>(response, cancellationToken);
    }

    public async Task<FiscalConfigApiResponse> GetFiscalConfigWithApiKeyAsync(
        string? apiKey,
        int deviceId,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "api/fiscal-config" + DeviceQuery(deviceId));

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            // HttpClient copies a default header onto a request only when the request does not already
            // carry it, so setting it here is what keeps the configured key out of this call — which is
            // the whole point of the method.
            request.Headers.TryAddWithoutValidation("X-API-Key", apiKey.Trim());
        }
        else
        {
            // No candidate key means the installed one goes, so there had better be one.
            ThrowIfNoApiKey();
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken);

        return await ReadResponseAsync<FiscalConfigApiResponse>(response, cancellationToken);
    }

    public async Task<FiscalStatusApiResponse> GetFiscalStatusAsync(
        int deviceId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfNoApiKey();

        using var response = await _httpClient.GetAsync(
            "api/fiscal-status" + DeviceQuery(deviceId),
            cancellationToken);

        return await ReadResponseAsync<FiscalStatusApiResponse>(response, cancellationToken);
    }

    /// <summary>
    /// Refuses a call outright when there is no key to send. Same status and exception type the platform
    /// would answer with, so every caller's handling of a refused key still applies — it just no longer
    /// costs a round trip to learn what the settings already say.
    /// </summary>
    private void ThrowIfNoApiKey()
    {
        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            throw new FiscalisationApiException(
                HttpStatusCode.Unauthorized,
                ApiKeyNotConfiguredErrorCode,
                "No Fiscalisation API key is configured; set Fiscalisation__ApiKey. The request was not sent.");
        }
    }

    /// <summary>
    /// The device selector for a read, left off entirely when no device is pinned.
    /// </summary>
    /// <remarks>
    /// The platform takes this as a nullable parameter and falls back to its own configured device only
    /// when it is <em>absent</em>. Sending <c>deviceId=0</c> is not the same thing: it is a supplied
    /// value, so the fallback is skipped and the request is refused with "DeviceId is required and must
    /// be greater than 0". That is why an unpinned deployment could read nothing at all — including the
    /// settings screen's key test, which reported a perfectly good key as unverifiable.
    ///
    /// Submissions are the other way round and deliberately so: there a zero device id means "any of
    /// them", and the platform walks every configured device until one succeeds.
    /// </remarks>
    private static string DeviceQuery(int deviceId) =>
        deviceId > 0
            ? $"?deviceId={deviceId.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            : string.Empty;

    /// <summary>
    /// One attempt, no retry.
    /// </summary>
    /// <remarks>
    /// For calls that change nothing and are only advisory. Retrying a preflight ten times would delay
    /// the submission it is meant to protect by longer than the submission itself takes, to answer a
    /// question the platform will ask again anyway when the receipt arrives.
    /// </remarks>
    private async Task<TResponse> PostOnceAsync<TRequest, TResponse>(
        string requestUri,
        TRequest request,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            requestUri, request, ApiJsonOptions, cancellationToken);

        return await ReadResponseAsync<TResponse>(response, cancellationToken);
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

        // Whether the platform said anything at all, which is how a caller tells a refusal it should act
        // on from a route this platform build does not serve.
        throw new FiscalisationApiException(
            response.StatusCode, error?.ErrorCode, detail, hasProblemDocument: error is not null);
    }
}
