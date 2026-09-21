using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Tests;

/// <summary>
/// An <see cref="IWebhookService"/> that records what was published and delivers nothing.
/// </summary>
/// <remarks>
/// Shared because <see cref="ShopInventory.Services.NotificationService"/> now publishes webhook
/// events, so every test that builds one needs this whether or not it cares about webhooks. The CRUD
/// members throw rather than returning empty: nothing on this path should be calling them, and a
/// silent empty list would hide a test wired to the wrong seam.
/// </remarks>
public sealed class RecordingWebhookService : IWebhookService
{
    private readonly List<(string EventType, object Payload)> _published = [];

    public IReadOnlyList<(string EventType, object Payload)> Published => _published;

    public Task TriggerEventAsync(string eventType, object payload)
    {
        _published.Add((eventType, payload));
        return Task.CompletedTask;
    }

    public Task<List<WebhookDto>> GetAllWebhooksAsync() => throw new NotSupportedException();

    public Task<WebhookDto?> GetWebhookByIdAsync(int id) => throw new NotSupportedException();

    public Task<WebhookDto> CreateWebhookAsync(CreateWebhookRequest request) => throw new NotSupportedException();

    public Task<WebhookDto?> UpdateWebhookAsync(int id, UpdateWebhookRequest request) => throw new NotSupportedException();

    public Task<bool> DeleteWebhookAsync(int id) => throw new NotSupportedException();

    public Task<TestWebhookResponse> TestWebhookAsync(int id, TestWebhookRequest request) => throw new NotSupportedException();

    public Task<WebhookDeliveryListResponse> GetDeliveriesAsync(int? webhookId = null, int page = 1, int pageSize = 50)
        => throw new NotSupportedException();

    public Task<List<WebhookEventTypeInfo>> GetEventTypesAsync() => throw new NotSupportedException();
}
