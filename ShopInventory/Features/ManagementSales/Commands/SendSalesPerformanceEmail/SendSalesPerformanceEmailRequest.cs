namespace ShopInventory.Features.ManagementSales.Commands.SendSalesPerformanceEmail;

/// <summary>The body of <c>POST api/Report/sales-performance/email</c>.</summary>
/// <param name="Cadence">daily, weekly or monthly for the last complete period, custom for the dates given.</param>
/// <param name="FromDate">First day of a custom period.</param>
/// <param name="ToDate">Last day of a custom period.</param>
/// <param name="Recipients">Who to send to; empty sends to the schedule's list.</param>
public sealed record SendSalesPerformanceEmailRequest(
    string Cadence,
    DateTime? FromDate,
    DateTime? ToDate,
    List<string>? Recipients);
