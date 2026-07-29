using System.Net.Http.Json;
using System.Text.Json;
using ShopInventory.Web.Models;

namespace ShopInventory.Web.Services;

public interface IWhatsAppAdminService
{
    Task<WhatsAppHealthModel> GetHealthAsync(CancellationToken cancellationToken = default);
    Task<WhatsAppInboxResponseModel> GetInboxAsync(int page = 1, int pageSize = 25, string? search = null, CancellationToken cancellationToken = default);
    Task<WhatsAppSessionModel> CreateSessionAsync(string sessionName, CancellationToken cancellationToken = default);
    Task<List<WhatsAppSessionModel>> GetSessionsAsync(CancellationToken cancellationToken = default);
    Task<WhatsAppSessionModel> StartSessionAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<WhatsAppSessionModel> StopSessionAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<WhatsAppQrCodeModel?> TryGetSessionQrCodeAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<WhatsAppMessageDispatchModel> SendTextAsync(string sessionId, string chatId, string text, CancellationToken cancellationToken = default);
    Task<WhatsAppMessageDispatchModel> ReplyAsync(string sessionId, string chatId, string quotedMessageId, string text, CancellationToken cancellationToken = default);
}

public sealed class WhatsAppAdminService(
    HttpClient httpClient,
    ILogger<WhatsAppAdminService> logger) : IWhatsAppAdminService
{
    public async Task<WhatsAppHealthModel> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await httpClient.GetAsync("api/whatsapp/health", cancellationToken);
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

        var response = await httpClient.GetAsync(requestUri, cancellationToken);
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

    public async Task<List<WhatsAppSessionModel>> GetSessionsAsync(CancellationToken cancellationToken = default)
    {
        var response = await httpClient.GetAsync("api/whatsapp/sessions", cancellationToken);
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
        var response = await httpClient.GetAsync($"api/whatsapp/sessions/{Uri.EscapeDataString(sessionId)}/qr", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogInformation("QR code is not available for WhatsApp session {SessionId}: {StatusCode} {Body}", sessionId, response.StatusCode, body);
            return null;
        }

        return await response.Content.ReadFromJsonAsync<WhatsAppQrCodeModel>(cancellationToken);
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
            BaseUrl = message,
            CheckedAtUtc = DateTime.UtcNow,
            SourcePath = "n/a"
        };
    }

    private async Task<T> SendForJsonAsync<T>(string requestUri, object? payload, CancellationToken cancellationToken)
    {
        using var response = payload is null
            ? await httpClient.PostAsync(requestUri, content: null, cancellationToken)
            : await httpClient.PostAsJsonAsync(requestUri, payload, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogWarning("WhatsApp operator request failed: {RequestUri} {StatusCode} {Body}", requestUri, response.StatusCode, body);
            throw new HttpRequestException(BuildApiFailureMessage(response.StatusCode, response.ReasonPhrase, body));
        }

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
            ?? throw new HttpRequestException("The API returned an empty WhatsApp response payload.");
    }

    private static string BuildApiFailureMessage(System.Net.HttpStatusCode statusCode, string? reasonPhrase, string? responseBody)
    {
        var parsedMessage = TryExtractMessage(responseBody);
        if (!string.IsNullOrWhiteSpace(parsedMessage))
        {
            return parsedMessage;
        }

        return $"WhatsApp request failed. API returned {(int)statusCode} {reasonPhrase}.";
    }

    private static string? TryExtractMessage(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return null;
        }

        try
        {
            using var jsonDocument = JsonDocument.Parse(responseBody);
            var root = jsonDocument.RootElement;

            if (TryReadString(root, "title", out var title))
            {
                return title;
            }

            if (TryReadString(root, "detail", out var detail))
            {
                return detail;
            }

            if (TryReadString(root, "message", out var message))
            {
                return message;
            }

            if (TryReadString(root, "error", out var error))
            {
                return error;
            }

            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in errors.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.Array)
                    {
                        var firstError = property.Value.EnumerateArray()
                            .FirstOrDefault(item => item.ValueKind == JsonValueKind.String)
                            .GetString();

                        if (!string.IsNullOrWhiteSpace(firstError))
                        {
                            return firstError;
                        }
                    }
                }
            }
        }
        catch (JsonException)
        {
        }

        return responseBody.Trim();
    }

    private static bool TryReadString(JsonElement element, string propertyName, out string? value)
    {
        value = null;

        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return !string.IsNullOrWhiteSpace(value);
    }
}