namespace ShopInventory.Features.DesktopIntegration.Queries.GetDesktopSalesAnalysis;

/// <summary>One business day's takings, split by payment method.</summary>
/// <remarks>
/// <para><c>ByPaymentMethod</c>: One entry per method in the analysis' column order, zeros
/// included.</para>
/// </remarks>
public sealed record DesktopSalesDayRow(
    DateTime Date,
    int SalesCount,
    decimal TotalAmount,
    decimal VatAmount,
    List<DesktopSalesPaymentAmount> ByPaymentMethod);
