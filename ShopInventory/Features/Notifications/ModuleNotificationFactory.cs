using System.Globalization;
using ShopInventory.DTOs;

namespace ShopInventory.Features.Notifications;

internal static class ModuleNotificationFactory
{
    /// <summary>
    /// "CHE020 - Cheese Galore", or whichever half is present. Shared so every module's
    /// notification names a business partner the same way.
    /// </summary>
    public static string DescribeBusinessPartner(string? cardCode, string? cardName)
    {
        var normalizedCode = cardCode?.Trim();
        var normalizedName = cardName?.Trim();

        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return string.IsNullOrWhiteSpace(normalizedCode) ? "unknown account" : normalizedCode;
        }

        return string.IsNullOrWhiteSpace(normalizedCode)
            ? normalizedName
            : $"{normalizedCode} - {normalizedName}";
    }

    /// <summary>
    /// An amount for the prose half of a notification — grouped and currency-prefixed, for reading.
    /// The machine-readable copy belongs in <c>Data</c>, invariant and ungrouped.
    /// </summary>
    public static string DescribeMoney(string? currency, decimal amount)
        => string.IsNullOrWhiteSpace(currency)
            ? amount.ToString("N2", CultureInfo.InvariantCulture)
            : $"{currency.Trim()} {amount.ToString("N2", CultureInfo.InvariantCulture)}";

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