namespace ShopInventory.Web.Features.Reports.Queries.GetDesktopSalesAnalysis;

/// <summary>The takings of one shop, one business partner, one source or one operator, split by payment method.</summary>
public sealed class DesktopSalesBreakdownRow
{
    public string Key { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public int SalesCount { get; set; }

    public decimal TotalAmount { get; set; }

    public decimal VatAmount { get; set; }

    public decimal ShareOfValuePercent { get; set; }

    public List<DesktopSalesPaymentAmount> ByPaymentMethod { get; set; } = [];

    /// <summary>
    /// What has been credited back on these sales, tax-inclusive like <see cref="TotalAmount"/>; 0 when
    /// nothing has. <see cref="TotalAmount"/> is not reduced by it: it stays what was rung up.
    /// </summary>
    public decimal CreditedAmount { get; set; }

    /// <summary><see cref="TotalAmount"/> less <see cref="CreditedAmount"/>, as the API states it.</summary>
    public decimal NetOfCredits { get; set; }
}
