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
    List<DesktopSalesPaymentAmount> ByPaymentMethod)
{
    /// <summary>
    /// What has been credited back on these sales, tax-inclusive like <c>TotalAmount</c>; 0 when nothing
    /// has. <c>TotalAmount</c> is not reduced by it: it stays what was rung up.
    /// </summary>
    public decimal CreditedAmount { get; init; }

    /// <summary><c>TotalAmount</c> less <see cref="CreditedAmount"/>: what these sales stand at.</summary>
    public decimal NetOfCredits => TotalAmount - CreditedAmount;
}
