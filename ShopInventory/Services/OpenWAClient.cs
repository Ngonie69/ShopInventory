using System.Globalization;
using System.Text.Json;
using System.Net.Http.Json;
using System.Net;
using Microsoft.Extensions.Options;
using ShopInventory.Configuration;
using ShopInventory.DTOs;

namespace ShopInventory.Services;

/// <summary>
/// Typed HTTP client for the OpenWA WhatsApp gateway.
/// </summary>
public sealed class OpenWAClient(
    HttpClient httpClient,
    IOptions<OpenWASettings> settings,
    ILogger<OpenWAClient> logger) : IOpenWAClient
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly OpenWASettings _settings = settings.Value;
    private readonly ILogger<OpenWAClient> _logger = logger;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<WhatsAppHealthDto?> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        var candidatePaths = _settings.HealthEndpointPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.StartsWith('/') ? path : $"/{path}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (candidatePaths.Length == 0)
        {
            throw new InvalidOperationException("No OpenWA health endpoints are configured.");
        }

        var attemptErrors = new List<string>();

        foreach (var path in candidatePaths)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, path);

                if (!string.IsNullOrWhiteSpace(_settings.ApiKey))
                {
                    request.Headers.TryAddWithoutValidation("X-API-Key", _settings.ApiKey);
                }

                using var response = await _httpClient.SendAsync(request, cancellationToken);
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    attemptErrors.Add($"{path}: {(int)response.StatusCode} {response.ReasonPhrase}");
                    continue;
                }

                return ParseHealthResponse(path, responseBody);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "OpenWA health check failed against {Path}", path);
                attemptErrors.Add($"{path}: {ex.Message}");
            }
        }

        throw new HttpRequestException(
            $"Unable to reach OpenWA health endpoint at {_httpClient.BaseAddress}. Attempts: {string.Join(" | ", attemptErrors)}");
    }

    public Task<WhatsAppSessionDto> CreateSessionAsync(
        WhatsAppCreateSessionRequestDto request,
        CancellationToken cancellationToken = default)
    {
        return SendForJsonAsync<WhatsAppSessionDto>(HttpMethod.Post, "/api/sessions", request, cancellationToken);
    }

    public Task<List<WhatsAppSessionDto>> GetSessionsAsync(CancellationToken cancellationToken = default)
    {
        return SendForJsonAsync<List<WhatsAppSessionDto>>(HttpMethod.Get, "/api/sessions", payload: null, cancellationToken);
    }

    public Task<WhatsAppSessionDto> StartSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        return SendForJsonAsync<WhatsAppSessionDto>(HttpMethod.Post, $"/api/sessions/{Uri.EscapeDataString(sessionId)}/start", payload: null, cancellationToken);
    }

    public Task<WhatsAppSessionDto> StopSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        return SendForJsonAsync<WhatsAppSessionDto>(HttpMethod.Post, $"/api/sessions/{Uri.EscapeDataString(sessionId)}/stop", payload: null, cancellationToken);
    }

    public Task<WhatsAppQrCodeDto> GetSessionQrCodeAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        return SendForJsonAsync<WhatsAppQrCodeDto>(HttpMethod.Get, $"/api/sessions/{Uri.EscapeDataString(sessionId)}/qr", payload: null, cancellationToken);
    }

    public Task<WhatsAppMessageDispatchDto> SendTextAsync(
        string sessionId,
        WhatsAppSendTextRequestDto request,
        CancellationToken cancellationToken = default)
    {
        return SendForJsonAsync<WhatsAppMessageDispatchDto>(
            HttpMethod.Post,
            $"/api/sessions/{Uri.EscapeDataString(sessionId)}/messages/send-text",
            request,
            cancellationToken);
    }

    public Task<WhatsAppMessageDispatchDto> ReplyAsync(
        string sessionId,
        WhatsAppReplyRequestDto request,
        CancellationToken cancellationToken = default)
    {
        return SendForJsonAsync<WhatsAppMessageDispatchDto>(
            HttpMethod.Post,
            $"/api/sessions/{Uri.EscapeDataString(sessionId)}/messages/reply",
            request,
            cancellationToken);
    }

    private WhatsAppHealthDto ParseHealthResponse(string sourcePath, string? responseBody)
    {
        var health = new WhatsAppHealthDto
        {
            BaseUrl = _httpClient.BaseAddress?.ToString().TrimEnd('/') ?? _settings.BaseUrl.TrimEnd('/'),
            SourcePath = sourcePath,
            Status = "ok",
            CheckedAtUtc = DateTime.UtcNow
        };

        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return health;
        }

        using var jsonDocument = JsonDocument.Parse(responseBody);
        var root = jsonDocument.RootElement;

        health.Status = ReadString(root, "status") ?? health.Status;
        health.Version = ReadString(root, "version");
        health.UptimeSeconds = ReadDouble(root, "uptime");
        health.ReportedAtUtc = ReadDateTime(root, "timestamp");

        if (root.TryGetProperty("checks", out var checks) && checks.ValueKind == JsonValueKind.Object)
        {
            if (checks.TryGetProperty("sessions", out var sessions) && sessions.ValueKind == JsonValueKind.Object)
            {
                health.TotalSessions = ReadInt(sessions, "total");
                health.ConnectedSessions = ReadInt(sessions, "connected");
                health.DisconnectedSessions = ReadInt(sessions, "disconnected");
            }
        }

        return health;
    }

    private async Task<T> SendForJsonAsync<T>(
        HttpMethod method,
        string path,
        object? payload,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(method, path, payload);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        return await ReadJsonResponseAsync<T>(response, cancellationToken);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, object? payload)
    {
        var normalizedPath = path.StartsWith('/') ? path : $"/{path}";
        var request = new HttpRequestMessage(method, normalizedPath);

        if (!string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            request.Headers.TryAddWithoutValidation("X-API-Key", _settings.ApiKey);
        }

        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload, options: SerializerOptions);
        }

        return request;
    }

    private static async Task<T> ReadJsonResponseAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new OpenWAGatewayException(response.StatusCode, response.ReasonPhrase, responseBody);
        }

        if (string.IsNullOrWhiteSpace(responseBody))
        {
            throw new HttpRequestException("OpenWA returned an empty response body.");
        }

        var payload = JsonSerializer.Deserialize<T>(responseBody, SerializerOptions);
        return payload ?? throw new HttpRequestException("OpenWA returned an unreadable JSON payload.");
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static double? ReadDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetDouble(out var value) => value,
            JsonValueKind.String when double.TryParse(property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) => value,
            _ => null
        };
    }

    private static int? ReadInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var value) => value,
            JsonValueKind.String when int.TryParse(property.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) => value,
            _ => null
        };
    }

    private static DateTime? ReadDateTime(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return DateTime.TryParse(property.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var value)
            ? value
            : null;
    }
}