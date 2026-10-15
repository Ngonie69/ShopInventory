namespace ShopInventory.Features.ManagementSales.Commands.SendSalesPerformanceEmail;

/// <summary>What a send did.</summary>
/// <remarks><list type="table">
/// <item><term>Skipped</term><description>Nothing was sent, on purpose — switched off, already sent, or too late to catch up. <c>Reason</c> says which.</description></item>
/// <item><term>Failed</term><description>The addresses the mail server did not take.</description></item>
/// <item><term>FindingCount</term><description>How many findings the email carried across all three lines of business.</description></item>
/// </list></remarks>
public sealed record SalesPerformanceEmailResult(
    string Cadence,
    DateTime FromDate,
    DateTime ToDate,
    List<string> SentTo,
    List<string> Failed,
    bool Skipped,
    string? Reason,
    int FindingCount);
