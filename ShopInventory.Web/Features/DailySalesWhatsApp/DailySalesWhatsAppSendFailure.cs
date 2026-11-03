// Mirrors ShopInventory/Features/DailySalesWhatsApp/Commands/SendDailySalesWhatsApp/DailySalesWhatsAppSendResult.cs.

namespace ShopInventory.Web.Features.DailySalesWhatsApp;

public sealed class DailySalesWhatsAppSendFailure
{
    public string Number { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}
