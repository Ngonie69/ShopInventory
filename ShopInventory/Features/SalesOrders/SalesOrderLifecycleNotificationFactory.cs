using ShopInventory.DTOs;
using ShopInventory.Models;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.SalesOrders;

internal static class SalesOrderLifecycleNotificationFactory
{
    private const string MobileSalesActionUrl = "/mobile-drafts";
    private static readonly string[] StaffRoles = ["Admin", "Cashier"];

    public static CreateNotificationRequest? CreateCreatorNotification(
        SalesOrderDto order,
        string title,
        string message,
        string type,
        IReadOnlyDictionary<string, string>? data = null)
    {
        if (order.Source != SalesOrderSource.Mobile || string.IsNullOrWhiteSpace(order.CreatedByUserName))
        {
            return null;
        }

        return CreateNotification(
            order,
            title,
            message,
            type,
            data,
            targetUserId: order.CreatedByUserId,
            targetUsername: order.CreatedByUserName);
    }

    public static IReadOnlyList<CreateNotificationRequest> CreateStaffNotifications(
        SalesOrderDto order,
        string title,
        string message,
        string type,
        IReadOnlyDictionary<string, string>? data = null)
    {
        if (order.Source != SalesOrderSource.Mobile)
        {
            return [];
        }

        return StaffRoles
            .Select(role => CreateNotification(order, title, message, type, data, targetRole: role))
            .ToArray();
    }

    private static CreateNotificationRequest CreateNotification(
        SalesOrderDto order,
        string title,
        string message,
        string type,
        IReadOnlyDictionary<string, string>? data,
        Guid? targetUserId = null,
        string? targetUsername = null,
        string? targetRole = null)
    {
        var notificationData = BuildData(order, data);

        return new CreateNotificationRequest
        {
            Title = title,
            Message = message,
            Type = type,
            Category = "SalesOrder",
            EntityType = "SalesOrder",
            EntityId = order.OrderNumber,
            ActionUrl = MobileSalesActionUrl,
            TargetUserId = targetUserId,
            TargetUsername = targetUsername,
            TargetRole = targetRole,
            Data = notificationData
        };
    }

    private static Dictionary<string, string> BuildData(
        SalesOrderDto order,
        IReadOnlyDictionary<string, string>? data)
    {
        var notificationData = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["orderId"] = order.Id.ToString(),
            ["orderNumber"] = order.OrderNumber,
            ["cardCode"] = order.CardCode,
            ["customerCode"] = order.CardCode,
            ["customerName"] = ResolveCustomerName(order),
            ["status"] = order.Status.ToString()
        };

        if (data is null)
        {
            return notificationData;
        }

        foreach (var entry in data)
        {
            notificationData[entry.Key] = entry.Value;
        }

        return notificationData;
    }

    private static string ResolveCustomerName(SalesOrderDto order)
        => string.IsNullOrWhiteSpace(order.CardName)
            ? order.CardCode
            : order.CardName;
}