// Mirrors the parts of ShopInventory/Features/DailySalesWhatsApp/Queries/GetDailySalesWhatsAppReport/DailySalesWhatsAppReport.cs
// that the settings card shows.

namespace ShopInventory.Web.Features.DailySalesWhatsApp;

public sealed class DailySalesWhatsAppPreview
{
    public DateTime TradingDate { get; set; }
    public string Message { get; set; } = string.Empty;
}
