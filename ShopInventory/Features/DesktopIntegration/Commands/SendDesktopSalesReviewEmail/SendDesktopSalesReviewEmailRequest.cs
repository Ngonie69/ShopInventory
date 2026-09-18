namespace ShopInventory.Features.DesktopIntegration.Commands.SendDesktopSalesReviewEmail;

/// <summary>The body of <c>POST api/DesktopIntegration/sales/review/email</c>.</summary>
/// <param name="Cadence">weekly or monthly for the last complete period, custom for the dates given.</param>
/// <param name="FromDate">First day of a custom period.</param>
/// <param name="ToDate">Last day of a custom period.</param>
/// <param name="Recipients">Who to send to; empty sends to the schedule's list.</param>
public sealed record SendDesktopSalesReviewEmailRequest(
    string Cadence,
    DateTime? FromDate,
    DateTime? ToDate,
    List<string>? Recipients);
