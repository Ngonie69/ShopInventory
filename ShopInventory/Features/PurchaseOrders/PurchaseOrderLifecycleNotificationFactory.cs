using ShopInventory.DTOs;
using ShopInventory.Features.Notifications;
using ShopInventory.Models.Entities;

namespace ShopInventory.Features.PurchaseOrders;

internal static class PurchaseOrderLifecycleNotificationFactory
{
    private const string ActionUrl = "/purchase-orders";

    public static CreateNotificationRequest CreateNotification(
        PurchaseOrderDto order,
        string title,
        string message,
        string type,
        IReadOnlyDictionary<string, string>? data = null)
    {
        var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["purchaseOrderId"] = order.Id.ToString(),
            ["orderNumber"] = order.OrderNumber,
            ["sapDocEntry"] = order.SAPDocEntry?.ToString() ?? string.Empty,
            ["sapDocNum"] = order.SAPDocNum?.ToString() ?? string.Empty,
            ["cardCode"] = order.CardCode,
            ["cardName"] = order.CardName ?? string.Empty,
            ["currency"] = order.Currency ?? string.Empty,
            ["docTotal"] = order.DocTotal.ToString("N2"),
            ["status"] = order.Status.ToString()
        };

        if (data is not null)
        {
            foreach (var entry in data)
            {
                payload[entry.Key] = entry.Value;
            }
        }

        return ModuleNotificationFactory.CreateBroadcastNotification(
            title,
            message,
            type,
            "PurchaseOrder",
            "PurchaseOrder",
            order.SAPDocEntry?.ToString() ?? order.Id.ToString(),
            ActionUrl,
            payload);
    }

    public static string BuildBusinessPartnerDisplay(string? cardCode, string? cardName)
    {
        var normalizedCode = cardCode?.Trim();
        var normalizedName = cardName?.Trim();

        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return normalizedCode ?? "unknown supplier";
        }

        if (string.IsNullOrWhiteSpace(normalizedCode))
        {
            return normalizedName;
        }

        return $"{normalizedCode} - {normalizedName}";
    }

    public static string BuildMoneyDisplay(string? currency, decimal total)
        => string.IsNullOrWhiteSpace(currency)
            ? total.ToString("N2")
            : $"{currency} {total:N2}";

    public static string BuildStatusLabel(PurchaseOrderStatus status)
        => status switch
        {
            PurchaseOrderStatus.PartiallyReceived => "Partially Received",
            PurchaseOrderStatus.OnHold => "On Hold",
            _ => status.ToString()
        };

    public static string GetTypeForStatus(PurchaseOrderStatus status)
        => status switch
        {
            PurchaseOrderStatus.Cancelled => "Warning",
            PurchaseOrderStatus.OnHold => "Warning",
            PurchaseOrderStatus.Draft => "Info",
            PurchaseOrderStatus.Pending => "Info",
            _ => "Success"
        };
}