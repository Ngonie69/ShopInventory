using System.Net.Http.Json;
using Blazored.LocalStorage;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

public interface IWhatsAppAdminService
{
    Task<WhatsAppHealthModel> GetHealthAsync(CancellationToken cancellationToken = default);
    Task<WhatsAppInboxResponseModel> GetInboxAsync(int page = 1, int pageSize = 25, string? search = null, CancellationToken cancellationToken = default);
    Task<string> GetRawPayloadViewAsync(int messageId, CancellationToken cancellationToken = default);
    Task<WhatsAppSessionModel> CreateSessionAsync(string sessionName, CancellationToken cancellationToken = default);
    Task<List<WhatsAppSessionModel>> GetSessionsAsync(CancellationToken cancellationToken = default);
    Task<WhatsAppSessionModel> StartSessionAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<WhatsAppSessionModel> StopSessionAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<WhatsAppQrCodeModel?> TryGetSessionQrCodeAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<WhatsAppWebhookStatusModel?> TryGetSessionWebhookAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<WhatsAppWebhookStatusModel> EnsureSessionWebhookAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<WhatsAppMessageDispatchModel> SendTextAsync(string sessionId, string chatId, string text, CancellationToken cancellationToken = default);
    Task<WhatsAppMessageDispatchModel> ReplyAsync(string sessionId, string chatId, string quotedMessageId, string text, CancellationToken cancellationToken = default);
}

/// <remarks>
/// Every call goes through <see cref="SendAuthenticatedAsync"/>. The console refreshes itself
/// every fifteen seconds and is left open for hours, so it outlives the access token it was opened
/// with: sent bare, each call after that is a 401, the session list reads as empty and the
/// operator is told to sign in again while the rest of the app still works.
/// </remarks>
public sealed class WhatsAppAdminService(
    HttpClient httpClient,
    ILogger<WhatsAppAdminService> logger,
    ILocalStorageService localStorage,
    CustomAuthStateProvider authStateProvider) : IWhatsAppAdminService
{
    public async Task<WhatsAppHealthModel> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await SendAuthenticatedAsync(() => httpClient.GetAsync("api/whatsapp/health", cancellationToken));
            if (response.IsSuccessStatusCode)
            {
                var payload = await response.Content.ReadFromJsonAsync<WhatsAppHealthModel>(cancellationToken);
                return payload ?? BuildFallbackHealth("No health payload was returned by the API.");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogWarning("Failed to fetch WhatsApp health: {StatusCode} {Body}", response.StatusCode, body);
            return BuildFallbackHealth(BuildApiFailureMessage(response.StatusCode, response.ReasonPhrase, body));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch WhatsApp health status from API");
            return BuildFallbackHealth("API is unreachable. Check the OpenWA host and API connectivity.");
        }
    }

    public async Task<WhatsAppInboxResponseModel> GetInboxAsync(int page = 1, int pageSize = 25, string? search = null, CancellationToken cancellationToken = default)
    {
        var requestUri = $"api/whatsapp/messages?page={Math.Max(page, 1)}&pageSize={Math.Clamp(pageSize, 1, 200)}";
        if (!string.IsNullOrWhiteSpace(search))
        {
            requestUri += $"&search={Uri.EscapeDataString(search.Trim())}";
        }

        var response = await SendAuthenticatedAsync(() => httpClient.GetAsync(requestUri, cancellationToken));
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogWarning("Failed to fetch WhatsApp inbox: {StatusCode} {Body}", response.StatusCode, body);
            throw new HttpRequestException(BuildApiFailureMessage(response.StatusCode, response.ReasonPhrase, body));
        }

        return await response.Content.ReadFromJsonAsync<WhatsAppInboxResponseModel>(cancellationToken)
            ?? new WhatsAppInboxResponseModel
            {
                Page = Math.Max(page, 1),
                PageSize = Math.Clamp(pageSize, 1, 200)
            };
    }

    /// <summary>
    /// One message's raw payload as the inbox shows it, an attachment's base64 cut out
    /// (<see cref="WhatsAppRawPayloadView"/>). The list does not carry the payload at all.
    /// </summary>
    public async Task<string> GetRawPayloadViewAsync(int messageId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthenticatedAsync(() => httpClient.GetAsync($"api/whatsapp/messages/{messageId}/raw", cancellationToken));
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogWarning("Failed to fetch the raw payload of WhatsApp message {MessageId}: {StatusCode} {Body}", messageId, response.StatusCode, body);
            throw new HttpRequestException(BuildApiFailureMessage(response.StatusCode, response.ReasonPhrase, body));
        }

        // Read as bytes, never as a string: a 27 MB body would be 54 MB of UTF-16 before the cut.
        var utf8Body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return WhatsAppRawPayloadView.Render(utf8Body);
    }

    public async Task<List<WhatsAppSessionModel>> GetSessionsAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAuthenticatedAsync(() => httpClient.GetAsync("api/whatsapp/sessions", cancellationToken));
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogWarning("Failed to fetch WhatsApp sessions: {StatusCode} {Body}", response.StatusCode, body);
            throw new HttpRequestException(BuildApiFailureMessage(response.StatusCode, response.ReasonPhrase, body));
        }

        return await response.Content.ReadFromJsonAsync<List<WhatsAppSessionModel>>(cancellationToken)
            ?? [];
    }

    public Task<WhatsAppSessionModel> CreateSessionAsync(string sessionName, CancellationToken cancellationToken = default)
    {
        return SendForJsonAsync<WhatsAppSessionModel>(
            "api/whatsapp/sessions",
            new
            {
                name = sessionName
            },
            cancellationToken);
    }

    public Task<WhatsAppSessionModel> StartSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        return SendForJsonAsync<WhatsAppSessionModel>($"api/whatsapp/sessions/{Uri.EscapeDataString(sessionId)}/start", payload: null, cancellationToken);
    }

    public Task<WhatsAppSessionModel> StopSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        return SendForJsonAsync<WhatsAppSessionModel>($"api/whatsapp/sessions/{Uri.EscapeDataString(sessionId)}/stop", payload: null, cancellationToken);
    }

    public async Task<WhatsAppQrCodeModel?> TryGetSessionQrCodeAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var response = await SendAuthenticatedAsync(() => httpClient.GetAsync($"api/whatsapp/sessions/{Uri.EscapeDataString(sessionId)}/qr", cancellationToken));
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogInformation("QR code is not available for WhatsApp session {SessionId}: {StatusCode} {Body}", sessionId, response.StatusCode, body);
            return null;
        }

        return await response.Content.ReadFromJsonAsync<WhatsAppQrCodeModel>(cancellationToken);
    }

    public async Task<WhatsAppWebhookStatusModel?> TryGetSessionWebhookAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        // Read-only, and deliberately non-throwing: the delivery panel is a diagnostic beside the
        // session, and a gateway that will not answer must not take the session list down with it.
        try
        {
            var response = await SendAuthenticatedAsync(() => httpClient.GetAsync($"api/whatsapp/sessions/{Uri.EscapeDataString(sessionId)}/webhook", cancellationToken));
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                logger.LogInformation(
                    "Webhook status is unavailable for WhatsApp session {SessionId}: {StatusCode} {Body}",
                    sessionId,
                    response.StatusCode,
                    body);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<WhatsAppWebhookStatusModel>(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read the webhook status for WhatsApp session {SessionId}", sessionId);
            return null;
        }
    }

    public Task<WhatsAppWebhookStatusModel> EnsureSessionWebhookAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        return SendForJsonAsync<WhatsAppWebhookStatusModel>(
            $"api/whatsapp/sessions/{Uri.EscapeDataString(sessionId)}/webhook",
            payload: null,
            cancellationToken);
    }

    public Task<WhatsAppMessageDispatchModel> SendTextAsync(
        string sessionId,
        string chatId,
        string text,
        CancellationToken cancellationToken = default)
    {
        return SendForJsonAsync<WhatsAppMessageDispatchModel>(
            $"api/whatsapp/sessions/{Uri.EscapeDataString(sessionId)}/messages/send-text",
            new
            {
                chatId,
                text
            },
            cancellationToken);
    }

    public Task<WhatsAppMessageDispatchModel> ReplyAsync(
        string sessionId,
        string chatId,
        string quotedMessageId,
        string text,
        CancellationToken cancellationToken = default)
    {
        return SendForJsonAsync<WhatsAppMessageDispatchModel>(
            $"api/whatsapp/sessions/{Uri.EscapeDataString(sessionId)}/messages/reply",
            new
            {
                chatId,
                quotedMessageId,
                text
            },
            cancellationToken);
    }

    private static WhatsAppHealthModel BuildFallbackHealth(string message)
    {
        return new WhatsAppHealthModel
        {
            Status = "unreachable",
            BaseUrl = "Not reported",
            Message = message,
            CheckedAtUtc = DateTime.UtcNow,
            SourcePath = "n/a"
        };
    }

    private async Task<T> SendForJsonAsync<T>(string requestUri, object? payload, CancellationToken cancellationToken)
    {
        using var response = await SendAuthenticatedAsync(() => payload is null
            ? httpClient.PostAsync(requestUri, content: null, cancellationToken)
            : httpClient.PostAsJsonAsync(requestUri, payload, cancellationToken));

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogWarning("WhatsApp operator request failed: {RequestUri} {StatusCode} {Body}", requestUri, response.StatusCode, body);
            throw new HttpRequestException(BuildApiFailureMessage(response.StatusCode, response.ReasonPhrase, body));
        }

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
            ?? throw new HttpRequestException("The API returned an empty WhatsApp response payload.");
    }

    private Task<HttpResponseMessage> SendAuthenticatedAsync(Func<Task<HttpResponseMessage>> send)
        => ApiTokenAuthentication.SendAsync(httpClient, authStateProvider, localStorage, send, logger);

    /// <summary>
    /// Turns an API problem-details body into something an operator can act on.
    /// </summary>
    /// <remarks>
    /// This defers to <see cref="ApiErrorResponse"/> rather than reading the body itself. The
    /// reason it matters here: the API answers a WhatsApp configuration fault with
    /// ValidationProblemDetails, whose title is always the framework's "One or more validation
    /// errors occurred." and whose real cause ("WhatsApp integration is disabled") sits in
    /// "errors". Reading "title" first, as this service used to, threw the cause away and left the
    /// console reporting a validation failure for a request that had nothing wrong with it.
    /// </remarks>
    private static string BuildApiFailureMessage(System.Net.HttpStatusCode statusCode, string? reasonPhrase, string? responseBody)
    {
        return ApiErrorResponse.GetFriendlyMessage(
            statusCode,
            responseBody,
            $"WhatsApp request failed. API returned {(int)statusCode} {reasonPhrase}.");
    }
}