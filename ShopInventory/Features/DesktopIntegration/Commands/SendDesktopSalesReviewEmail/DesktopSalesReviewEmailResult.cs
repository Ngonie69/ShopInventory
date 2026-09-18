namespace ShopInventory.Features.DesktopIntegration.Commands.SendDesktopSalesReviewEmail;

/// <summary>What a send did.</summary>
/// <remarks><list type="table">
/// <item><term>Skipped</term><description>Nothing was sent, on purpose — switched off, already sent, or too late to catch up. <c>Reason</c> says which.</description></item>
/// <item><term>Failed</term><description>The addresses the mail server did not take.</description></item>
/// </list></remarks>
public sealed record DesktopSalesReviewEmailResult(
    string Cadence,
    DateTime FromDate,
    DateTime ToDate,
    List<string> SentTo,
    List<string> Failed,
    bool Skipped,
    string? Reason,
    int FindingCount);
