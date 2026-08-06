using MediatR;
using ShopInventory.Services;

namespace ShopInventory.Features.Merchandiser.Events.ProductCatalogChanged;

/// <summary>
/// Tells merchandiser devices their product list has changed, without telling the merchandiser.
/// </summary>
/// <remarks>
/// This used to raise a notification: "Product catalog updated — Item GOU015 was deactivated. Your
/// product catalog will refresh automatically." A merchandiser has nothing to do with that. What
/// they are answerable for is the orders they submitted, and that is what their notifications are
/// now confined to; a catalogue change is a signal for the app, not news for the person carrying
/// it. So it goes out as a data-only push — the app is woken and handed the item codes, and nothing
/// appears in the tray.
/// </remarks>
public sealed class ProductCatalogChangedNotificationHandler(
    IPushNotificationService pushService,
    ILogger<ProductCatalogChangedNotificationHandler> logger
) : INotificationHandler<ProductCatalogChangedEvent>
{
    private const string MerchandiserRole = "Merchandiser";

    public async Task Handle(ProductCatalogChangedEvent notification, CancellationToken cancellationToken)
    {
        if (notification.ItemCodes.Count == 0)
            return;

        var itemCodes = notification.ItemCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (itemCodes.Count == 0)
            return;

        var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["changeType"] = "ProductCatalog",
            ["itemCodes"] = string.Join(",", itemCodes),
            ["itemCount"] = itemCodes.Count.ToString(),
            ["isActive"] = notification.IsActive ? "true" : "false",
            ["changedAtUtc"] = notification.ChangedAtUtc.ToString("O")
        };

        try
        {
            var sent = await pushService.SendSilentDataToRoleAsync(MerchandiserRole, data, cancellationToken);

            logger.LogInformation(
                "Signalled a product catalog change for {ItemCount} item(s) to {DeviceCount} merchandiser device(s); IsActive={IsActive}",
                itemCodes.Count,
                sent,
                notification.IsActive);
        }
        catch (Exception ex)
        {
            // The catalogue is already updated and the app re-reads it on its own schedule, so a
            // failed signal costs freshness, not correctness — not worth failing the status change
            // that has already been committed.
            logger.LogError(
                ex,
                "Failed to signal a product catalog change for {ItemCount} item(s)",
                itemCodes.Count);
        }
    }
}
