using MediatR;
using ShopInventory.DTOs;
using ShopInventory.Services;

namespace ShopInventory.Features.Merchandiser.Events.ProductCatalogChanged;

public sealed class ProductCatalogChangedNotificationHandler(
    INotificationService notificationService,
    ILogger<ProductCatalogChangedNotificationHandler> logger
) : INotificationHandler<ProductCatalogChangedEvent>
{
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

        var title = "Product catalog updated";
        var action = notification.IsActive ? "activated" : "deactivated";
        var message = itemCodes.Count == 1
            ? $"Item {itemCodes[0]} was {action}. Your product catalog will refresh automatically."
            : $"{itemCodes.Count} products were {action}. Your product catalog will refresh automatically.";

        var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["changeType"] = "ProductCatalog",
            ["itemCodes"] = string.Join(",", itemCodes),
            ["itemCount"] = itemCodes.Count.ToString(),
            ["isActive"] = notification.IsActive ? "true" : "false",
            ["changedAtUtc"] = notification.ChangedAtUtc.ToString("O")
        };

        await notificationService.CreateNotificationAsync(new CreateNotificationRequest
        {
            Title = title,
            Message = message,
            Type = notification.IsActive ? "Info" : "Warning",
            Category = "ProductCatalog",
            EntityType = "ProductCatalog",
            EntityId = itemCodes.Count == 1 ? itemCodes[0] : $"count:{itemCodes.Count}",
            TargetRole = "Merchandiser",
            Data = data
        }, cancellationToken);

        logger.LogInformation(
            "Published product catalog change notification for {ItemCount} item(s); IsActive={IsActive}",
            itemCodes.Count,
            notification.IsActive);
    }
}