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

    /// <summary>SAP's booked gross profit on this row's posted sales; null when none of them is costed or margin was not asked for.</summary>
    public decimal? GrossProfit { get; init; }

    /// <summary>The value before VAT <see cref="GrossProfit"/> was measured on: the posted part of this row.</summary>
    public decimal? CostedNetAmount { get; init; }

    /// <summary>How many of this row's sales <see cref="GrossProfit"/> was measured on.</summary>
    public int CostedSalesCount { get; init; }

    /// <summary><see cref="GrossProfit"/> as a percentage of <see cref="CostedNetAmount"/>.</summary>
    public decimal? MarginPercent => GrossProfit is { } profit && CostedNetAmount is { } costed && costed != 0
        ? Math.Round(profit / costed * 100m, 1, MidpointRounding.AwayFromZero)
        : null;
}
