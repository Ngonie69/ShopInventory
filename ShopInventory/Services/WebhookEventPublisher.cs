namespace ShopInventory.Services;

/// <summary>
/// Raises a webhook event from code that cannot hold the scoped <see cref="IWebhookService"/>.
/// </summary>
/// <remarks>
/// <see cref="IWebhookService"/> is scoped, because it reads and writes through the scoped
/// <c>ApplicationDbContext</c>. The places that most need to publish are not: the SAP client is a
/// typed <c>HttpClient</c> that background jobs resolve as well as requests, and the health and sync
/// machinery is singleton. This owns a scope per publish so they do not have to.
///
/// Never throws. Every caller here has already done the thing the event describes — the transfer is
/// in SAP, the sync has run — and a subscriber that cannot be reached must not turn that into a
/// failure. Delivery itself is fire-and-forget inside <see cref="IWebhookService.TriggerEventAsync"/>,
/// so what this awaits is only the subscriber lookup.
/// </remarks>
public sealed class WebhookEventPublisher(
    IServiceScopeFactory scopeFactory,
    ILogger<WebhookEventPublisher> logger)
{
    public async Task PublishAsync(string eventType, object payload)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var webhooks = scope.ServiceProvider.GetRequiredService<IWebhookService>();
            await webhooks.TriggerEventAsync(eventType, payload);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to publish webhook event {EventType}", eventType);
        }
    }
}
