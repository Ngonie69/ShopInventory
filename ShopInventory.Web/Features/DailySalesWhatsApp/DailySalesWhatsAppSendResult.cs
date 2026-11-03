// Mirrors ShopInventory/Features/DailySalesWhatsApp/Commands/SendDailySalesWhatsApp/DailySalesWhatsAppSendResult.cs.

namespace ShopInventory.Web.Features.DailySalesWhatsApp;

public sealed class DailySalesWhatsAppSendResult
{
    public DateTime TradingDate { get; set; }
    public List<string> SentTo { get; set; } = [];
    public List<string> Unconfirmed { get; set; } = [];
    public List<DailySalesWhatsAppSendFailure> Failed { get; set; } = [];
    public bool Skipped { get; set; }
    public string? Reason { get; set; }
    public string? Message { get; set; }
}
