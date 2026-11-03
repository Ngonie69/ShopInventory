using ShopInventory.Features.DailySalesWhatsApp.Queries.GetDailySalesWhatsAppSettings;

namespace ShopInventory.Features.DailySalesWhatsApp.Commands.UpdateDailySalesWhatsAppSettings;

/// <summary>The body of <c>PUT api/daily-sales-whatsapp/settings</c>.</summary>
public sealed record UpdateDailySalesWhatsAppSettingsRequest(
    bool Enabled,
    List<string>? Recipients,
    string? ShopWarehouseCode,
    string? ShopHeading,
    List<DailySalesWhatsAppRow>? VanRows,
    List<DailySalesWhatsAppRow>? ProductRows);
