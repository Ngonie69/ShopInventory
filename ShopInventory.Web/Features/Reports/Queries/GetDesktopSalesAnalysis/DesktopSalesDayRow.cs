namespace ShopInventory.Web.Features.Reports.Queries.GetDesktopSalesAnalysis;

/// <summary>One business day's takings, split by payment method.</summary>
public sealed class DesktopSalesDayRow
{
    public DateTime Date { get; set; }

    public int SalesCount { get; set; }

    public decimal TotalAmount { get; set; }

    public decimal VatAmount { get; set; }

    public List<DesktopSalesPaymentAmount> ByPaymentMethod { get; set; } = [];

    /// <summary>
    /// What has been credited back on these sales, tax-inclusive like <see cref="TotalAmount"/>; 0 when
    /// nothing has. <see cref="TotalAmount"/> is not reduced by it: it stays what was rung up.
    /// </summary>
    public decimal CreditedAmount { get; set; }

    /// <summary><see cref="TotalAmount"/> less <see cref="CreditedAmount"/>, as the API states it.</summary>
    public decimal NetOfCredits { get; set; }
}
