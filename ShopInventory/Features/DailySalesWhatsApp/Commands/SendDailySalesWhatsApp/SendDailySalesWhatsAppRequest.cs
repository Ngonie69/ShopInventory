namespace ShopInventory.Features.DailySalesWhatsApp.Commands.SendDailySalesWhatsApp;

/// <summary>The body of <c>POST api/daily-sales-whatsapp/send</c>.</summary>
/// <param name="TradingDate">The day to report; yesterday when null.</param>
/// <param name="Recipients">Numbers to send to; the saved ones when null or empty.</param>
public sealed record SendDailySalesWhatsAppRequest(DateTime? TradingDate, List<string>? Recipients);
