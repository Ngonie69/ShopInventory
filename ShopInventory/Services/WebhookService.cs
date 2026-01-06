using Microsoft.EntityFrameworkCore;
using ShopInventory.Data;
using ShopInventory.DTOs;
using ShopInventory.Models;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ShopInventory.Services;

/// <summary>
/// Service for managing webhooks and delivering webhook events
/// </summary>
public interface IWebhookService
{
    Task<List<WebhookDto>> GetAllWebhooksAsync();
    Task<WebhookDto?> GetWebhookByIdAsync(int id);
    Task<WebhookDto> CreateWebhookAsync(CreateWebhookRequest request);
    Task<WebhookDto?> UpdateWebhookAsync(int id, UpdateWebhookRequest request);
    Task<bool> DeleteWebhookAsync(int id);
    Task<TestWebhookResponse> TestWebhookAsync(int id, TestWebhookRequest request);
    Task TriggerEventAsync(string eventType, object payload);
    Task<WebhookDeliveryListResponse> GetDeliveriesAsync(int? webhookId = null, int page = 1, int pageSize = 50);
    Task<List<WebhookEventTypeInfo>> GetEventTypesAsync();
}

public class WebhookService : IWebhookService
{
    private readonly ApplicationDbContext _context;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<WebhookService> _logger;

    public WebhookService(
        ApplicationDbContext context,
        IHttpClientFactory httpClientFactory,
        ILogger<WebhookService> logger)
    {
        _context = context;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<List<WebhookDto>> GetAllWebhooksAsync()
    {
        var webhooks = await _context.Webhooks.ToListAsync();
        return webhooks.Select(MapToDto).ToList();
    }

    public async Task<WebhookDto?> GetWebhookByIdAsync(int id)
    {
        var webhook = await _context.Webhooks.FindAsync(id);
        return webhook == null ? null : MapToDto(webhook);
    }

    public async Task<WebhookDto> CreateWebhookAsync(CreateWebhookRequest request)
    {
        // Validate event types
        var invalidEvents = request.Events.Where(e => !WebhookEventTypes.All.Contains(e)).ToList();
        if (invalidEvents.Any())
        {
            throw new ArgumentException($"Invalid event types: {string.Join(", ", invalidEvents)}");
        }

        var webhook = new Webhook
        {
            Name = request.Name,
            Url = request.Url,
            Secret = request.Secret,
            Events = string.Join(",", request.Events),
            RetryCount = request.RetryCount,
            TimeoutSeconds = request.TimeoutSeconds,
            CustomHeaders = request.CustomHeaders != null ? JsonSerializer.Serialize(request.CustomHeaders) : null,
            IsActive = true
        };

        _context.Webhooks.Add(webhook);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Created webhook {Name} (ID: {Id}) for events: {Events}",
            webhook.Name, webhook.Id, webhook.Events);

        return MapToDto(webhook);
    }

    public async Task<WebhookDto?> UpdateWebhookAsync(int id, UpdateWebhookRequest request)
    {
        var webhook = await _context.Webhooks.FindAsync(id);
        if (webhook == null) return null;

        if (request.Name != null) webhook.Name = request.Name;
        if (request.Url != null) webhook.Url = request.Url;
        if (request.Secret != null) webhook.Secret = request.Secret;
        if (request.Events != null)
        {
            var invalidEvents = request.Events.Where(e => !WebhookEventTypes.All.Contains(e)).ToList();
            if (invalidEvents.Any())
            {
                throw new ArgumentException($"Invalid event types: {string.Join(", ", invalidEvents)}");
            }
            webhook.Events = string.Join(",", request.Events);
        }
        if (request.IsActive.HasValue) webhook.IsActive = request.IsActive.Value;
        if (request.RetryCount.HasValue) webhook.RetryCount = request.RetryCount.Value;
        if (request.TimeoutSeconds.HasValue) webhook.TimeoutSeconds = request.TimeoutSeconds.Value;
        if (request.CustomHeaders != null) webhook.CustomHeaders = JsonSerializer.Serialize(request.CustomHeaders);

        await _context.SaveChangesAsync();

        _logger.LogInformation("Updated webhook {Name} (ID: {Id})", webhook.Name, webhook.Id);

        return MapToDto(webhook);
    }

    public async Task<bool> DeleteWebhookAsync(int id)
    {
        var webhook = await _context.Webhooks.FindAsync(id);
        if (webhook == null) return false;

        _context.Webhooks.Remove(webhook);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Deleted webhook {Name} (ID: {Id})", webhook.Name, id);

        return true;
    }

    public async Task<TestWebhookResponse> TestWebhookAsync(int id, TestWebhookRequest request)
    {
        var webhook = await _context.Webhooks.FindAsync(id);
        if (webhook == null)
        {
            return new TestWebhookResponse
            {
                Success = false,
                ErrorMessage = "Webhook not found"
            };
        }

        var payload = new
        {
            id = Guid.NewGuid().ToString(),
            @event = request.EventType,
            timestamp = DateTime.UtcNow,
            isTest = true,
            data = request.SampleData ?? new { message = "This is a test webhook delivery" }
        };

        return await DeliverWebhookAsync(webhook, request.EventType, payload, isTest: true);
    }

    public async Task TriggerEventAsync(string eventType, object payload)
    {
        var webhooks = await _context.Webhooks
            .Where(w => w.IsActive && w.Events.Contains(eventType))
            .ToListAsync();

        if (!webhooks.Any())
        {
            _logger.LogDebug("No active webhooks subscribed to event: {EventType}", eventType);
            return;
        }

        var webhookPayload = new
        {
            id = Guid.NewGuid().ToString(),
            @event = eventType,
            timestamp = DateTime.UtcNow,
            data = payload
        };

        foreach (var webhook in webhooks)
        {
            // Fire and forget - don't wait for webhook delivery
            _ = Task.Run(async () =>
            {
                try
                {
                    await DeliverWebhookWithRetryAsync(webhook, eventType, webhookPayload);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error delivering webhook {Id} for event {Event}", webhook.Id, eventType);
                }
            });
        }

        _logger.LogInformation("Triggered event {EventType} to {Count} webhooks", eventType, webhooks.Count);
    }

    public async Task<WebhookDeliveryListResponse> GetDeliveriesAsync(int? webhookId = null, int page = 1, int pageSize = 50)
    {
        var query = _context.WebhookDeliveries
            .Include(d => d.Webhook)
            .AsQueryable();

        if (webhookId.HasValue)
        {
            query = query.Where(d => d.WebhookId == webhookId.Value);
        }

        var totalCount = await query.CountAsync();
        var deliveries = await query
            .OrderByDescending(d => d.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(d => new WebhookDeliveryDto
            {
                Id = d.Id,
                WebhookId = d.WebhookId,
                WebhookName = d.Webhook.Name,
                EventType = d.EventType,
                ResponseStatusCode = d.ResponseStatusCode,
                IsSuccess = d.IsSuccess,
                ErrorMessage = d.ErrorMessage,
                DurationMs = d.DurationMs,
                RetryAttempt = d.RetryAttempt,
                CreatedAt = d.CreatedAt
            })
            .ToListAsync();

        return new WebhookDeliveryListResponse
        {
            Deliveries = deliveries,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
            TotalPages = (int)Math.Ceiling(totalCount / (double)pageSize)
        };
    }

    public Task<List<WebhookEventTypeInfo>> GetEventTypesAsync()
    {
        var eventTypes = new List<WebhookEventTypeInfo>
        {
            new() { EventType = WebhookEventTypes.InvoiceCreated, Category = "Invoice", Description = "Triggered when a new invoice is created" },
            new() { EventType = WebhookEventTypes.InvoicePaid, Category = "Invoice", Description = "Triggered when an invoice is fully paid" },
            new() { EventType = WebhookEventTypes.InvoiceCancelled, Category = "Invoice", Description = "Triggered when an invoice is cancelled" },
            new() { EventType = WebhookEventTypes.PaymentReceived, Category = "Payment", Description = "Triggered when a payment is received" },
            new() { EventType = WebhookEventTypes.PaymentFailed, Category = "Payment", Description = "Triggered when a payment fails" },
            new() { EventType = WebhookEventTypes.PaymentRefunded, Category = "Payment", Description = "Triggered when a payment is refunded" },
            new() { EventType = WebhookEventTypes.StockLow, Category = "Stock", Description = "Triggered when stock falls below minimum level" },
            new() { EventType = WebhookEventTypes.StockOut, Category = "Stock", Description = "Triggered when stock reaches zero" },
            new() { EventType = WebhookEventTypes.StockReplenished, Category = "Stock", Description = "Triggered when stock is replenished" },
            new() { EventType = WebhookEventTypes.StockTransfer, Category = "Stock", Description = "Triggered when stock is transferred between warehouses" },
            new() { EventType = WebhookEventTypes.InventoryAdjusted, Category = "Inventory", Description = "Triggered when inventory is adjusted" },
            new() { EventType = WebhookEventTypes.InventoryReceived, Category = "Inventory", Description = "Triggered when inventory is received" },
            new() { EventType = WebhookEventTypes.CustomerCreated, Category = "Customer", Description = "Triggered when a new customer is created" },
            new() { EventType = WebhookEventTypes.CustomerUpdated, Category = "Customer", Description = "Triggered when a customer is updated" },
            new() { EventType = WebhookEventTypes.SapSyncSuccess, Category = "SAP", Description = "Triggered when SAP sync completes successfully" },
            new() { EventType = WebhookEventTypes.SapSyncFailed, Category = "SAP", Description = "Triggered when SAP sync fails" },
            new() { EventType = WebhookEventTypes.SapConnectionLost, Category = "SAP", Description = "Triggered when SAP connection is lost" },
            new() { EventType = WebhookEventTypes.SapConnectionRestored, Category = "SAP", Description = "Triggered when SAP connection is restored" }
        };

        return Task.FromResult(eventTypes);
    }

    private async Task DeliverWebhookWithRetryAsync(Webhook webhook, string eventType, object payload)
    {
        var maxAttempts = webhook.RetryCount;
        var attempt = 0;

        while (attempt < maxAttempts)
        {
            attempt++;
            var result = await DeliverWebhookAsync(webhook, eventType, payload, retryAttempt: attempt);

            if (result.Success)
            {
                webhook.SuccessCount++;
                webhook.LastTriggeredAt = DateTime.UtcNow;
                await _context.SaveChangesAsync();
                return;
            }

            if (attempt < maxAttempts)
            {
                // Exponential backoff: 2^attempt seconds
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                _logger.LogWarning("Webhook delivery failed for {Id}, retrying in {Delay}s (attempt {Attempt}/{Max})",
                    webhook.Id, delay.TotalSeconds, attempt, maxAttempts);
                await Task.Delay(delay);
            }
        }

        // All retries exhausted
        webhook.FailureCount++;
        webhook.LastTriggeredAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        _logger.LogError("Webhook delivery failed for {Id} after {Attempts} attempts", webhook.Id, maxAttempts);
    }

    private async Task<TestWebhookResponse> DeliverWebhookAsync(Webhook webhook, string eventType, object payload, int retryAttempt = 1, bool isTest = false)
    {
        var stopwatch = Stopwatch.StartNew();
        var delivery = new WebhookDelivery
        {
            WebhookId = webhook.Id,
            EventType = eventType,
            Payload = JsonSerializer.Serialize(payload),
            RetryAttempt = retryAttempt
        };

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(webhook.TimeoutSeconds);

            var jsonPayload = JsonSerializer.Serialize(payload);
            var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            // Add custom headers
            if (!string.IsNullOrEmpty(webhook.CustomHeaders))
            {
                var customHeaders = JsonSerializer.Deserialize<Dictionary<string, string>>(webhook.CustomHeaders);
                if (customHeaders != null)
                {
                    foreach (var header in customHeaders)
                    {
                        client.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }
            }

            // Add signature header if secret is configured
            if (!string.IsNullOrEmpty(webhook.Secret))
            {
                var signature = ComputeHmacSignature(jsonPayload, webhook.Secret);
                client.DefaultRequestHeaders.TryAddWithoutValidation("X-Webhook-Signature", signature);
            }

            client.DefaultRequestHeaders.TryAddWithoutValidation("X-Webhook-Event", eventType);
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-Webhook-Delivery-Id", Guid.NewGuid().ToString());

            var response = await client.PostAsync(webhook.Url, content);
            stopwatch.Stop();

            delivery.ResponseStatusCode = (int)response.StatusCode;
            delivery.ResponseBody = await response.Content.ReadAsStringAsync();
            delivery.IsSuccess = response.IsSuccessStatusCode;
            delivery.DurationMs = stopwatch.ElapsedMilliseconds;

            if (!isTest)
            {
                _context.WebhookDeliveries.Add(delivery);
                await _context.SaveChangesAsync();
            }

            return new TestWebhookResponse
            {
                Success = response.IsSuccessStatusCode,
                StatusCode = (int)response.StatusCode,
                ResponseBody = delivery.ResponseBody,
                DurationMs = delivery.DurationMs
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            delivery.IsSuccess = false;
            delivery.ErrorMessage = ex.Message;
            delivery.DurationMs = stopwatch.ElapsedMilliseconds;

            if (!isTest)
            {
                _context.WebhookDeliveries.Add(delivery);
                await _context.SaveChangesAsync();
            }

            _logger.LogError(ex, "Error delivering webhook {Id} to {Url}", webhook.Id, webhook.Url);

            return new TestWebhookResponse
            {
                Success = false,
                ErrorMessage = ex.Message,
                DurationMs = delivery.DurationMs
            };
        }
    }

    private static string ComputeHmacSignature(string payload, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return $"sha256={Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private static WebhookDto MapToDto(Webhook webhook)
    {
        return new WebhookDto
        {
            Id = webhook.Id,
            Name = webhook.Name,
            Url = webhook.Url,
            Events = webhook.Events.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(),
            IsActive = webhook.IsActive,
            RetryCount = webhook.RetryCount,
            TimeoutSeconds = webhook.TimeoutSeconds,
            CustomHeaders = !string.IsNullOrEmpty(webhook.CustomHeaders)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(webhook.CustomHeaders)
                : null,
            CreatedAt = webhook.CreatedAt,
            LastTriggeredAt = webhook.LastTriggeredAt,
            SuccessCount = webhook.SuccessCount,
            FailureCount = webhook.FailureCount
        };
    }
}
