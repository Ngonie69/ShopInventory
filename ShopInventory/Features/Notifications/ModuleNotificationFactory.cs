using ShopInventory.DTOs;

namespace ShopInventory.Features.Notifications;

internal static class ModuleNotificationFactory
{
    public static CreateNotificationRequest CreateBroadcastNotification(
        string title,
        string message,
        string type,
        string category,
        string entityType,
        string entityId,
        string actionUrl,
        IReadOnlyDictionary<string, string>? data = null)
    {
        return new CreateNotificationRequest
        {
            Title = title,
            Message = message,
            Type = type,
            Category = category,
            EntityType = entityType,
            EntityId = entityId,
            ActionUrl = actionUrl,
            Data = data == null
                ? null
                : new Dictionary<string, string>(data, StringComparer.OrdinalIgnoreCase)
        };
    }
}