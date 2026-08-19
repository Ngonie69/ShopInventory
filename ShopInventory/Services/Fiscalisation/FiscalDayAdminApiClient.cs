using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;

namespace ShopInventory.Services.Fiscalisation;

/// <summary>
/// Typed HTTP client for the Fiscalisation platform's admin-authenticated routes.
/// </summary>
/// <remarks>
/// Signs in with the service account configured for the device each call names, caches that account's
/// bearer token in <see cref="FiscalDayServiceAccountTokenStore"/> until shortly before it expires, and
/// signs in again if the platform ever answers 401 — which it does for a token revoked before its stated
/// expiry.
///
/// The credential is chosen per device rather than per client because the platform reads a single
/// <c>FdmsDeviceId</c> claim off the token and refuses every other device: with one account and a fleet,
/// all but one handset collects a bodyless 403.
///
/// Nothing here retries a failed operation. That is not an omission: the two calls that matter, closing a
/// fiscal day and submitting an offline file, are not idempotent at FDMS, and the platform already
/// distinguishes "not sent" from "outcome unknown" in its error code. Deciding between them is the
/// lifecycle's job, not the transport's.
/// </remarks>
public sealed class FiscalDayAdminApiClient : IFiscalDayAdminApiClient
{
    /// <summary>
    /// The error code a call fails with when no service account is configured, raised before anything is
    /// sent. Mirrors <see cref="FiscalisationApiClient.ApiKeyNotConfiguredErrorCode"/> and exists for the
    /// same reason: the platform's answer to a credential-less request is a 401 every time, so the round
    /// trip only buys what the settings already said.
    /// </summary>
    public const string ServiceAccountNotConfiguredErrorCode = "FiscalDayServiceAccountNotConfigured";

    private static readonly JsonSerializerOptions ApiJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly FiscalDayServiceAccountTokenStore _tokenStore;
    private readonly FiscalisationSettings _settings;
    private readonly ILogger<FiscalDayAdminApiClient> _logger;

    public FiscalDayAdminApiClient(
        HttpClient httpClient,
        FiscalDayServiceAccountTokenStore tokenStore,
        IOptions<FiscalisationSettings> settings,
        ILogger<FiscalDayAdminApiClient> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        _settings = settings?.Value ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool IsConfigured => _settings.FiscalDay.HasAnyServiceAccount;

    public bool IsConfiguredForDevice(int deviceId)
        => _settings.FiscalDay.ServiceAccountFor(deviceId).IsConfigured;

    public Task<OpenDayApiResponse> OpenFiscalDayAsync(
        OpenDayApiRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return SendAsync<OpenDayApiResponse>(
            request.DeviceId,
            () => JsonRequest(HttpMethod.Post, "api/fiscal-day/open", request),
            cancellationToken);
    }

    public Task<CloseDayApiResponse> CloseFiscalDayAsync(
        CloseDayApiRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return SendAsync<CloseDayApiResponse>(
            request.DeviceId,
            () => JsonRequest(HttpMethod.Post, "api/fiscal-day/close", request),
            cancellationToken);
    }

    public Task<GenerateOfflineFileApiResponse> GenerateOfflineFileAsync(
        GenerateOfflineFileApiRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return SendAsync<GenerateOfflineFileApiResponse>(
            request.DeviceId,
            () => JsonRequest(HttpMethod.Post, "api/offline-files/generate", request),
            cancellationToken);
    }

    public Task<SubmitOfflineFileApiResponse> SubmitOfflineFileAsync(
        SubmitOfflineFileApiRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return SendAsync<SubmitOfflineFileApiResponse>(
            request.DeviceId,
            () => JsonRequest(HttpMethod.Post, "api/offline-files/submit", request),
            cancellationToken);
    }

    public Task<GetFileStatusApiResponse> GetOfflineFileStatusAsync(
        int deviceId,
        DateTime fileUploadedFrom,
        DateTime fileUploadedTill,
        CancellationToken cancellationToken = default)
    {
        // The platform requires both bounds and refuses the read without them, so they are positional here
        // rather than optional.
        var query = $"api/offline-files/status?deviceId={deviceId.ToString(CultureInfo.InvariantCulture)}"
                    + $"&fileUploadedFrom={Uri.EscapeDataString(FormatQueryDate(fileUploadedFrom))}"
                    + $"&fileUploadedTill={Uri.EscapeDataString(FormatQueryDate(fileUploadedTill))}";

        return SendAsync<GetFileStatusApiResponse>(
            deviceId,
            () => new HttpRequestMessage(HttpMethod.Get, query),
            cancellationToken);
    }

    /// <summary>
    /// Sends one request with the bearer token of the account configured for the device it names, signing
    /// in again and repeating it once if the platform answers 401.
    /// </summary>
    /// <remarks>
    /// The device decides the credential, not the client: the platform authorises each call against the
    /// device the request names and reads one <c>FdmsDeviceId</c> claim off the token to do it, so a fleet
    /// is a fleet of accounts.
    ///
    /// <paramref name="buildRequest"/> is a factory rather than a message because a sent
    /// <see cref="HttpRequestMessage"/> cannot be sent again, and the 401 path needs a second one.
    ///
    /// Repeating on a 401 is safe in a way that repeating on anything else is not: the platform's
    /// authorization runs before the endpoint, so a 401 proves the handler never ran and FDMS was never told
    /// anything. That is the only status of which that is true here.
    /// </remarks>
    private async Task<TResponse> SendAsync<TResponse>(
        int deviceId,
        Func<HttpRequestMessage> buildRequest,
        CancellationToken cancellationToken)
    {
        var serviceAccount = _settings.FiscalDay.ServiceAccountFor(deviceId);
        ThrowIfNoServiceAccount(deviceId, serviceAccount);

        var refreshSkew = TimeSpan.FromSeconds(Math.Max(0, serviceAccount.RefreshSkewSeconds));

        for (var attempt = 0; ; attempt++)
        {
            var token = await _tokenStore.GetAsync(
                serviceAccount.CacheKey,
                token => IssueTokenAsync(serviceAccount, token),
                refreshSkew,
                forceRefresh: attempt > 0,
                cancellationToken);

            using var request = buildRequest();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                _logger.LogInformation(
                    "The Fiscalisation platform refused the service account's token on {RequestUri}; signing in again.",
                    request.RequestUri);

                _tokenStore.Invalidate(serviceAccount.CacheKey);
                continue;
            }

            return await ReadResponseAsync<TResponse>(response, deviceId, serviceAccount, cancellationToken);
        }
    }

    /// <summary>
    /// Exchanges the configured username and password for a bearer token.
    /// </summary>
    /// <remarks>
    /// Deliberately verbose about a 403. The platform refuses a password token to an account holding the
    /// Admin role and to one that has not yet changed its one-time password, and both come back as the same
    /// bare 403 — a failure that looks like "wrong password" and is not, and that no amount of retrying
    /// resolves. Naming both here is the difference between a five-minute fix and an afternoon.
    /// </remarks>
    private async Task<JwtTokenApiResponse> IssueTokenAsync(
        FiscalDayServiceAccountSettings serviceAccount,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync(
            "auth/token",
            new JwtTokenApiRequest
            {
                Username = serviceAccount.Username.Trim(),
                Password = serviceAccount.Password
            },
            ApiJsonOptions,
            cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            var issued = await response.Content.ReadFromJsonAsync<JwtTokenApiResponse>(
                ApiJsonOptions, cancellationToken);

            if (issued is null)
            {
                throw new FiscalisationApiException(
                    response.StatusCode,
                    "ServiceAccountTokenEmpty",
                    "The Fiscalisation platform answered the token request with an empty body.");
            }

            return issued;
        }

        var detail = await ReadTokenFailureDetailAsync(response, cancellationToken);

        var guidance = response.StatusCode switch
        {
            HttpStatusCode.Forbidden =>
                " The platform refuses a password token to an account with the Admin role, and to one that still has a"
                + " one-time password to change. Give the fiscal-day service account a non-Admin role carrying the"
                + " fiscal-day and receipt-submit permissions, and sign in once interactively to clear the password change.",
            HttpStatusCode.Locked =>
                " The account is locked out after repeated failed sign-ins and will not issue a token until that clears.",
            _ => string.Empty
        };

        throw new FiscalisationApiException(
            response.StatusCode,
            "ServiceAccountTokenRejected",
            $"The Fiscalisation platform refused the fiscal-day service account '{serviceAccount.Username.Trim()}': "
            + $"{detail}{guidance}");
    }

    /// <summary>
    /// Reads the token route's failure body, which is not the platform's usual problem document — it answers
    /// <c>{ "error": "..." }</c> there — falling back to the raw body so nothing is swallowed.
    /// </summary>
    private static async Task<string> ReadTokenFailureDetailAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String)
            {
                return error.GetString() ?? body;
            }
        }
        catch (JsonException)
        {
            // Not JSON. The raw body, or the status, is the best that can be said.
        }

        return string.IsNullOrWhiteSpace(body)
            ? response.ReasonPhrase ?? $"HTTP {(int)response.StatusCode}"
            : body;
    }

    private static void ThrowIfNoServiceAccount(int deviceId, FiscalDayServiceAccountSettings serviceAccount)
    {
        if (!serviceAccount.IsConfigured)
        {
            throw new FiscalisationApiException(
                HttpStatusCode.Unauthorized,
                ServiceAccountNotConfiguredErrorCode,
                $"No Fiscalisation fiscal-day service account is configured for device {deviceId}; set "
                + $"Fiscalisation__FiscalDay__DeviceServiceAccounts__{deviceId}__Username and __Password, or "
                + "Fiscalisation__FiscalDay__ServiceAccount__Username and __Password for a single-device "
                + "deployment. The request was not sent.");
        }
    }

    private static HttpRequestMessage JsonRequest<TRequest>(HttpMethod method, string requestUri, TRequest body)
        => new(method, requestUri)
        {
            Content = JsonContent.Create(body, options: ApiJsonOptions)
        };

    /// <summary>
    /// The platform binds these as <see cref="DateTime"/> from the query string, so an unqualified sortable
    /// form is what it expects. A round-trip form would carry a zone the platform does not read and would
    /// shift the window by the host's offset.
    /// </summary>
    private static string FormatQueryDate(DateTime value)
        => value.ToString("s", CultureInfo.InvariantCulture);

    private async Task<T> ReadResponseAsync<T>(
        HttpResponseMessage response,
        int deviceId,
        FiscalDayServiceAccountSettings serviceAccount,
        CancellationToken cancellationToken)
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

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            detail += DescribeForbidden(deviceId, serviceAccount);

            // Also in the log, because the exception is truncated to 2,000 characters into a state row that
            // an operator may only ever see through the exception center.
            _logger.LogError(
                "The Fiscalisation platform refused service account '{Username}' for device {DeviceId} with 403 and "
                + "no body. It authorises each fiscal-day call against the device the request names, reading one "
                + "FdmsDeviceId claim from the token, so one account serves one device. {Guidance}",
                serviceAccount.Username.Trim(),
                deviceId,
                DescribeForbidden(deviceId, serviceAccount).Trim());
        }

        throw new FiscalisationApiException(
            response.StatusCode, error?.ErrorCode, detail, hasProblemDocument: error is not null);
    }

    /// <summary>
    /// Turns the platform's bodyless 403 into something an operator can act on.
    /// </summary>
    /// <remarks>
    /// The platform's device authorisation answers with no body at all, so without this the only thing
    /// recorded against the day is the word "Forbidden" — which reads as a wrong password and is almost
    /// never one. There are exactly three reasons it can say this, and naming the device and the account
    /// that were actually used is the difference between a five-minute fix and an afternoon.
    /// </remarks>
    private static string DescribeForbidden(int deviceId, FiscalDayServiceAccountSettings serviceAccount)
        => $" The platform authorises every fiscal-day call against the device named in the request and reads a"
           + $" single FdmsDeviceId claim from the bearer token to do it, so one account is authorised for one"
           + $" device. Account '{serviceAccount.Username.Trim()}' is not authorised for device {deviceId}:"
           + $" either its device claim names a different device, or its role does not carry the fiscal-day"
           + $" management, receipt-submit and report-view permissions. Configure a credential for this device"
           + $" under Fiscalisation__FiscalDay__DeviceServiceAccounts__{deviceId}__Username and __Password.";
}
