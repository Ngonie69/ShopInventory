using ErrorOr;
using MediatR;

namespace ShopInventory.Web.Features.DailySalesWhatsApp.Commands.UpdateDailySalesWhatsAppSettings;

/// <summary>Saves the daily sales WhatsApp report: its switch, numbers, shop, heading and the rows of both tables.</summary>
public sealed record UpdateDailySalesWhatsAppSettingsCommand(
    bool Enabled,
    List<string> Recipients,
    string? ShopWarehouseCode,
    string? ShopHeading,
    List<DailySalesWhatsAppRow> VanRows,
    List<DailySalesWhatsAppRow> ProductRows
) : IRequest<ErrorOr<DailySalesWhatsAppSettings>>;
