namespace ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesAnalysis;

/// <summary>
/// The takings of one shop, one source, or one operator, split by payment method.
/// </summary>
/// <remarks>
/// <para><c>Key</c>: What the row groups on — a warehouse code, a source system, or an account
/// id.</para>
/// <para><c>Label</c>: What to call it on a page.</para>
/// <para><c>ByPaymentMethod</c>: One entry per method in the analysis' column order, zeros
/// included.</para>
/// </remarks>
public sealed record DesktopSalesBreakdownRow(
    string Key,
    string Label,
    int SalesCount,
    decimal TotalAmount,
    decimal VatAmount,
    decimal ShareOfValuePercent,
    List<DesktopSalesPaymentAmount> ByPaymentMethod);
