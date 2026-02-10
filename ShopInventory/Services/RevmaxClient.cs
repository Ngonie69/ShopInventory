using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.Models.Revmax;

namespace ShopInventory.Services;

/// <summary>
/// Typed HTTP client implementation for REVMax fiscal API.
/// Handles HTTP communication with retry logic for transient failures.
/// </summary>
public class RevmaxClient : IRevmaxClient
{
    private readonly HttpClient _httpClient;
    private readonly RevmaxSettings _settings;
    private readonly ILogger<RevmaxClient> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    // HTTP status codes that should trigger retry
    private static readonly HashSet<HttpStatusCode> RetryableStatusCodes = new()
    {
        HttpStatusCode.RequestTimeout,       // 408
        (HttpStatusCode)429,                 // 429 Too Many Requests
        HttpStatusCode.InternalServerError,  // 500
        HttpStatusCode.BadGateway,           // 502
        HttpStatusCode.ServiceUnavailable,   // 503
        HttpStatusCode.GatewayTimeout        // 504
    };

    public RevmaxClient(
        HttpClient httpClient,
        IOptions<RevmaxSettings> settings,
        ILogger<RevmaxClient> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _settings = settings?.Value ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Configure HttpClient
        _httpClient.BaseAddress = new Uri(_settings.BaseUrl.TrimEnd('/') + "/");
        _httpClient.Timeout = TimeSpan.FromSeconds(_settings.TimeoutSeconds);
        _httpClient.DefaultRequestHeaders.Accept.Clear();
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // Configure JSON serialization - preserve exact property names
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = null, // Preserve exact names
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };
    }

    public async Task<CardDetailsResponse?> GetCardDetailsAsync(CancellationToken cancellationToken = default)
    {
        return await GetWithRetryAsync<CardDetailsResponse>("api/RevmaxAPI/GetCardDetails", cancellationToken);
    }

    public async Task<DayStatusResponse?> GetDayStatusAsync(CancellationToken cancellationToken = default)
    {
        return await GetWithRetryAsync<DayStatusResponse>("api/RevmaxAPI/GetDayStatus", cancellationToken);
    }

    public async Task<LicenseResponse?> GetLicenseAsync(CancellationToken cancellationToken = default)
    {
        return await GetWithRetryAsync<LicenseResponse>("api/RevmaxAPI/GetLicense", cancellationToken);
    }

    public async Task<LicenseResponse?> SetLicenseAsync(string license, CancellationToken cancellationToken = default)
    {
        var encodedLicense = Uri.EscapeDataString(license);
        return await GetWithRetryAsync<LicenseResponse>($"api/RevmaxAPI/SetLicense?license={encodedLicense}", cancellationToken);
    }

    public async Task<ZReportResponse?> GetZReportAsync(CancellationToken cancellationToken = default)
    {
        return await GetWithRetryAsync<ZReportResponse>("api/RevmaxAPI/ZReport", cancellationToken);
    }

    public async Task<InvoiceResponse?> GetInvoiceAsync(string invoiceNumber, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(invoiceNumber))
        {
            throw new ArgumentException("Invoice number is required", nameof(invoiceNumber));
        }

        var encodedInvoiceNumber = Uri.EscapeDataString(invoiceNumber);
        return await GetWithRetryAsync<InvoiceResponse>($"api/RevmaxAPI/GetInvoice/{encodedInvoiceNumber}", cancellationToken);
    }

    public async Task<UnprocessedInvoicesSummaryResponse?> GetUnprocessedInvoicesSummaryAsync(CancellationToken cancellationToken = default)
    {
        return await GetWithRetryAsync<UnprocessedInvoicesSummaryResponse>("api/RevmaxAPI/GetUnProcessedInvoicesSummary", cancellationToken);
    }

    public async Task<TransactMResponse?> TransactMAsync(TransactMRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        return await PostWithRetryAsync<TransactMRequest, TransactMResponse>("api/RevmaxAPI/TransactM", request, cancellationToken);
    }

    public async Task<TransactMExtResponse?> TransactMExtAsync(TransactMExtRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        return await PostWithRetryAsync<TransactMExtRequest, TransactMExtResponse>("api/RevmaxAPI/TransactMExt", request, cancellationToken);
    }

    public async Task<(HttpResponseMessage Response, string? Body)> GetRawAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        var response = await ExecuteWithRetryAsync(() => _httpClient.GetAsync(endpoint, cancellationToken), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return (response, body);
    }

    public async Task<(HttpResponseMessage Response, string? Body)> PostRawAsync(string endpoint, object? content, CancellationToken cancellationToken = default)
    {
        var jsonContent = content != null
            ? new StringContent(JsonSerializer.Serialize(content, _jsonOptions), Encoding.UTF8, "application/json")
            : null;

        var response = await ExecuteWithRetryAsync(() => _httpClient.PostAsync(endpoint, jsonContent, cancellationToken), cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return (response, body);
    }

    private async Task<T?> GetWithRetryAsync<T>(string endpoint, CancellationToken cancellationToken)
        where T : class
    {
        var response = await ExecuteWithRetryAsync(
            () => _httpClient.GetAsync(endpoint, cancellationToken),
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(content, _jsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize response from {Endpoint}: {Content}",
                endpoint, TruncateContent(content));
            throw;
        }
    }

    private async Task<TResponse?> PostWithRetryAsync<TRequest, TResponse>(
        string endpoint,
        TRequest request,
        CancellationToken cancellationToken)
        where TResponse : class
    {
        var jsonContent = new StringContent(
            JsonSerializer.Serialize(request, _jsonOptions),
            Encoding.UTF8,
            "application/json");

        var response = await ExecuteWithRetryAsync(
            () => _httpClient.PostAsync(endpoint, jsonContent, cancellationToken),
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<TResponse>(content, _jsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize response from {Endpoint}: {Content}",
                endpoint, TruncateContent(content));
            throw;
        }
    }

    private async Task<HttpResponseMessage> ExecuteWithRetryAsync(
        Func<Task<HttpResponseMessage>> operation,
        CancellationToken cancellationToken)
    {
        var maxRetries = _settings.MaxRetries;
        var delays = _settings.RetryDelaysMs;
        HttpResponseMessage? lastResponse = null;
        Exception? lastException = null;

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var response = await operation();
                lastResponse = response;

                // Check if response is retryable
                if (ShouldRetry(response.StatusCode) && attempt < maxRetries)
                {
                    var delayIndex = Math.Min(attempt, delays.Length - 1);
                    var delay = delays[delayIndex];

                    _logger.LogWarning(
                        "Request failed with status {StatusCode}. Retrying in {Delay}ms (attempt {Attempt}/{MaxRetries})",
                        (int)response.StatusCode, delay, attempt + 1, maxRetries);

                    await Task.Delay(delay, cancellationToken);
                    continue;
                }

                return response;
            }
            catch (HttpRequestException ex) when (attempt < maxRetries)
            {
                lastException = ex;
                var delayIndex = Math.Min(attempt, delays.Length - 1);
                var delay = delays[delayIndex];

                _logger.LogWarning(ex,
                    "HTTP request failed. Retrying in {Delay}ms (attempt {Attempt}/{MaxRetries})",
                    delay, attempt + 1, maxRetries);

                await Task.Delay(delay, cancellationToken);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested && attempt < maxRetries)
            {
                // Timeout - retry
                lastException = ex;
                var delayIndex = Math.Min(attempt, delays.Length - 1);
                var delay = delays[delayIndex];

                _logger.LogWarning(
                    "Request timed out. Retrying in {Delay}ms (attempt {Attempt}/{MaxRetries})",
                    delay, attempt + 1, maxRetries);

                await Task.Delay(delay, cancellationToken);
            }
        }

        // All retries exhausted
        if (lastResponse != null)
        {
            return lastResponse;
        }

        if (lastException != null)
        {
            throw lastException;
        }

        throw new InvalidOperationException("Unexpected retry loop exit");
    }

    private static bool ShouldRetry(HttpStatusCode statusCode)
    {
        // Retry only specific status codes
        // 408, 429, 5xx
        return RetryableStatusCodes.Contains(statusCode) ||
               (int)statusCode >= 500;
    }

    private static string TruncateContent(string content, int maxLength = 4096)
    {
        if (string.IsNullOrEmpty(content))
        {
            return content;
        }

        return content.Length <= maxLength
            ? content
            : content[..maxLength] + "... [truncated]";
    }
}
